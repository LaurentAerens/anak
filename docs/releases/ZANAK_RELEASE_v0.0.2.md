# Zanak v0.0.2 — Ply-1 Pruning Time Scaling & Tuned Time Management

**Zanak** is an experimental chess engine in the Anak repository built around **ZAP (Zero-branching Accelerated Probe)**.

---

## Highlights in `v0.0.2`

* **Single-Survivor Ply-1 Fast Exit (`ZapSearch`):** When `ZapRootFilter` narrows the root to a single surviving move (e.g., an obvious queen recapture) on a clock budget, Zanak seeds its Phase 1 spine, emits UCI telemetry, and returns immediately (`single-survivor`) instead of running Phase 2/3 workers.
* **Small-Survivor Time Scaling (`ZapSearch`, `TimeParams`):** When `2–4` root moves survive ply-1 filtering on a clock budget, `SoftLimitMs` is scaled down (`0.35x`, `0.50x`, `0.73x`) and `SmallSurvivorMinSoftFraction` (`0.15`) allows earlier dominance exits to bank clock time for complex positions.
* **Dominant-Leader Verification & Contender Quick-Check (`ZapSearch`, `ZapWorkQueue`, `ZapWorkerPool`, `ZapSharedState`):**
  * Tracks per-root completed crosspoints (`_rootCompletedCrosspoints`).
  * Fixes `GetSecondBestCandidate()` returning `Score = 0` when all other roots are dead stems (now returns sentinel `-30000`).
  * Once a dominant leader survives verification without being dethroned, Zanak runs a brief contender quick-check (`excludedRoot: leaderIdx`) across non-leader heaps; if no contender closes the gap, the search exits early (`dominant-leader-verified`) and skips the post-loop drain.
* **SPSA-Tuned `TimeParams` & UCI Exposure (`UciHandler`, `TimeParams`, `tools/tuning/spsa.py`):**
  * Exposed all live `TimeParams` early-exit and ply-1 survivor parameters as UCI `spin` options.
  * Tuned `TimeParams` over 2,400 `1+0.01` games (`60` SPSA steps, `Threads=2`).

---

## Verification & Benchmarks

* **Unit Tests:** `dotnet test -c Release --no-build -- xUnit.ParallelizeTestCollections=false` — **402 / 402 passed** (`0` failures, `0` skipped).
* **50-Game Benchmark vs `MinimalChess-0.2` (`1+0.01`, `openings.epd`):**
  * **Score:** `37.5 / 50` (**75.00%**, `36W - 11L - 3D`, `0` timeouts)
  * **Elo Diff:** **+190.85 ± 88.42** (`LOS: 100.00%`)
* **50-Game Benchmark vs `Stockfish-1320` (`UCI_LimitStrength=true`, `UCI_Elo=1320`, `1+0.01`, `openings.epd`):**
  * **Score:** `26.5 / 50` (**53.00%**, `25W - 22L - 3D`, `0` timeouts)
  * **Elo Diff:** **+20.87 ± 86.74** (`LOS: 68.53%`)
