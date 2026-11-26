# Anak v1.2.3 — Lockless TT, Persistent SMP Workers & 1024-Thread / 4TB Hash Scaling

**Release Date:** 2026-11-26  
**Category:** Multi-Threading / Scalability (Patch Release)  
**Estimated Elo:** **3264 &plusmn; 5** (1T: `+0.43 ± 5.70` logistic Elo / **+0 Elo** vs `v1.2.2`) | **`+6.69 ± 5.47` logistic Elo at `Threads=4`** (`99.18%` LOS)

## Summary

Scales Anak's Lazy SMP and Transposition Table architecture to **1024 threads** and **4 TB Hash** with lockless XOR-checksummed TT slots, persistent worker threads, per-worker `TbHits`, `ponderhit` clock support, and `ClearThreads`-gated parallel TT clearing.

## Benchmark Validation

- **4-Thread Match (`Anak v1.2.3` vs `Anak v1.2.2`, `1+0.01`, `Threads=4`, 8,000 games):**
  - **Score:** `4077.0 / 8000` (`50.96%` — `2525W / 2371L / 3104D`, `38.8%` draw rate)
  - **Elo:** **`+6.69 ± 5.47` logistic Elo** (`+9.32 ± 7.61` fastchess nElo / `+4.37 ± 5.39` `elo_kit` nElo, **`99.18%` LOS**, `0` timeouts vs `9`)
  - **PGN Archive:** [`anak_dev_vs_v1.2.2_20261125_202411.pgn`](../../tools/fastchess/results/anak_dev_vs_v1.2.2_20261125_202411.pgn)
- **1-Thread Match (`Anak v1.2.3` vs `Anak v1.2.2`, `1+0.01`, `Threads=1`, 8,000 games):**
  - **Score:** `4005.0 / 8000` (`50.06%` — `2643W / 2633L / 2724D`, `34.1%` draw rate)
  - **Elo:** `+0.43 ± 5.70` logistic Elo (`+0.58 ± 7.61` fastchess nElo / `-0.65 ± 5.38` `elo_kit` nElo, `55.94%` LOS → **+0 Elo** within error margin)
  - **PGN Archive:** [`anak_dev_vs_v1.2.2_20261125_223623.pgn`](../../tools/fastchess/results/anak_dev_vs_v1.2.2_20261125_223623.pgn)

## Highlights

- **Lockless XOR-Checksummed TT (`TTRawSlot`, `TTEntry.Pack`/`Unpack`):** 32-bit word-aligned 20-byte slots (`Pack = 4`) prevent torn TT reads/writes across up to 1024 threads and avoid 64-bit unaligned faults on ARM64.
- **Persistent Lazy SMP Worker Threads (`AlphaBetaEngine.cs`):** Replaced per-search `Task.Run` allocations with persistent `AutoResetEvent`-driven background workers (`AnakWorker-*`) and unconditional `_helpersDoneEvent.Wait()` / `_searchTask.GetAwaiter().GetResult()` synchronization.
- **`ClearThreads`-Gated `ParallelClear` (`TranspositionTable.cs`):** Parallel TT clearing only activates when `Threads > 1` and `Hash > 256 MB`, capped to `Threads`, eliminating ThreadPool thrashing on single-threaded or high-concurrency runs.
- **UCI & Syzygy Scaling (`UciOption.cs`, `UciHandler.cs`, `TimeManager.cs`, `SyzygyTablebase.cs`):** Raised `Threads` max to `1024` and `Hash` max to `4194304` MB (4 TB), added `TryProbeWdlNoCount` for lock-free per-worker `TbHits`, and added `PonderHit()` clock restart support.
