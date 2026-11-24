# Zanak `v1.1.0` — `ArchV2` Pairwise-1024 NNUE, Finny Tables & SPSA Search Retuning

**Zanak** is an experimental chess engine in the Anak repository built around **ZAP (Zero-branching Accelerated Probe)**.

`zanak-v1.1.0` upgrades Zanak's evaluation backend to Anak `v1.2.0`'s **ArchV2 (`0x0004`) Pairwise-1024 INT16 NNUE** with per-worker **Finny Tables**, and retunes Zanak's ZAP search parameters via SPSA for the new network's evaluation accuracy and node cost.

---

## Highlights in `v1.1.0`

* **ArchV2 (`0x0004`) Pairwise-1024 INT16 NNUE + Finny Tables (`ZanakNnueEvaluator`, `ZapSearch`):**
  * Replaced the legacy 6-expert INT8 MoE NNUE with Anak `v1.2.0`'s single unified `(768 × 32 HM) -> 1024×2 -> Pairwise-Mul(1024) -> 8` material-phase INT16 NNUE (`anak.nnue`), complete with true horizontal mirroring and per-worker `[2 × 32]` king-bucket Finny accumulator caching.
* **SPSA Search Retuning for `ArchV2` (`ZapParams.cs` — `+58.75 ± 30.45` Elo, `99.99% LOS`):**
  * Ran 2,400-game SPSA tuning (`--engine zanak --batch zanak_search`, 60 steps × 40 games at `2+0.02`, `Threads=1`) against `ArchV2`:
    * **`MaxSiblingExploration`:** `8 -> 4` (primary driver — halving per-pickup sibling expansion avoids starving deeper crosspoints under the heavier 1024-wide INT16 evaluator)
    * **`CrosspointRootBiasWeight`:** `23 -> 26`
    * **`ShockThresholdCp`:** `73 -> 75`, **`DeadStemPruneMarginCp`:** `247 -> 250`, **`HardAspirationMarginCp`:** `99 -> 101`, **`ContenderPromotionMarginCp`:** `50 -> 51`, **`WorkerPruneMarginCp`:** `201 -> 199`
    * **`AdaptiveShockBaseCp`:** `52 -> 51`, **`AdaptiveShockSlopeCp`:** `13 -> 11`, **`SpineAbandonThresholdCp`:** `250 -> 251`, **`RecaptureBonus`:** `2500 -> 2498`, **`EnPriseThreatPenalty`:** `4001 -> 4000`
  * Also tested a 2,000-game `zanak_time` SPSA batch (`-20.87 ± 31.08` Elo over 400 games) — rejected and reverted.

---

## Verification & Benchmarks (`Threads=1`, `openings.epd`)

* **Unit Tests:** `dotnet test -c Release` — **402 / 402 passed** (`0` failures, `0` skipped).
* **Self-Play Validation (`zanak-v1.1.0` vs pre-tuned `Zanak ArchV2`):**
  * `2+0.02` (`400` games): **`58.38%`** (`207W / 140L / 53D`) — **`+58.75 ± 30.45` logistic Elo** (`+67.14 ± 34.05` nElo, **`99.99% LOS`**).
* **Matches vs `Stockfish-1320` (`UCI_LimitStrength=true`, `UCI_Elo=1320`, `Threads=1`):**

| Time Control | Games | Score | W-L-D | Elo vs SF-1320 | Est. Elo | LOS |
|---|---:|---:|---:|---:|---:|---:|
| `1+0.01` | 400 | `62.38%` | `245-146-9` | `+87.81 ± 35.21` | **~1408** | `100.00%` |
| `5+0.05` | 100 | `39.00%` | `38-60-2` | `-77.71 ± 69.82` | **~1242** | `1.10%` |
