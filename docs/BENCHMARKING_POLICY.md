# Anak Benchmarking, Elo Evaluation & Testing Guide

> **Target Audience:** Engine developers, contributors, and maintainers.  
> **Related Files:** [`AGENTS.md`](../AGENTS.md) (strict AI rules), [`docs/v1.0.x-improvement-plan.md`](v1.0.x-improvement-plan.md) (current experiment log).

---

## 🎯 The Core Philosophy: Balancing Consistency, Compute & Real-Life Strength

In chess engine development, testing is often caught in a three-way tug-of-war:
1. **Consistency:** Not accepting noise, regressions, or false positives.
2. **Compute Budget:** Avoiding burning 3–4 hours of 100% CPU on every speculative change, draining laptop batteries, or causing thermal throttling.
3. **Real-Life Playing Strength:** Ensuring that optimizations don't just win at ultra-bullet (1+0.01 at depth 9), but translate into real Elo gains at tournament blitz and rapid depths (depth 14–22).

To solve this, Anak uses a **3-Tier Testing Pyramid** combined with **Feature Categorization** and **Fixed-Node testing options**.

---

## 🏛️ The Benchmarking & Validation Protocol

### 1. Pure NPS & Speedup Changes (Category A: Code Speed)
For refactoring, bitboards, memory layouts, SIMD vectorization, and caching optimizations intended purely to accelerate NPS without altering search decisions:
1. **Unit Test Pass:** 100% unit tests pass (`dotnet test -c Release`).
2. **Fixed-Node Determinism Check:** Search the 6 canonical benchmark positions at fixed depth (`anak bench 10`). Node counts and best moves must be **100% identical** to baseline.
3. **NPS Throughput & Memory Check:** Must show clear NPS speedup with **0 GC collections** (Gen 0, 1, and 2).
4. **Fast-Track Acceptance:** Pure NPS improvements that maintain 100% deterministic search trees and yield demonstrable NPS speedup without modifying search semantics are accepted directly or verified via a short fast confirmation run without requiring full 8,000-game match cycles.

### 2. Search & Evaluation Changes (Category B: Heuristics & Tree Pruning)
For pruning changes, extensions, history tables, and evaluation adjustments:
- Launch directly into **SPRT validation with early stopping**:
  ```bash
  python tools/fastchess/run_elo_bench.py --baseline <active_baseline> --sprt 0.0 3.0 --max-games 8000 --tc 1+0.01 --concurrency 8 --early-stop
  ```
- **Why no separate 250-game gate?** A separate 250-game gate has high variance ($\pm 40\text{ Elo}$) and requires stopping and restarting from 0 games. The `--early-stop` continuous monitor already catches severe regressions in ~400–600 games without throwing away positive progress on winning features.
- **Early Termination:**
  - Losers/regressions abort in 400–1,000 games.
  - Winners accept via SPRT in 1,500–2,500 games.

---

## ⏱️ Tackling the "Timeset Issue" (Wall-Clock vs. Fixed Nodes)

When evaluating chess engines on personal hardware (laptops, desktops with variable background load, or machines with battery power management), **wall-clock time controls (`1+0.01`) introduce two major sources of distortion**:

### Distortion 1: Thermal & Battery Throttling
- When a laptop transitions from wall power to battery, or when the CPU hits thermal throttling thresholds, CPU clocks drop by 20–40%.
- A match run on battery might search at 180k NPS, while the same build on desktop searches at 340k NPS. This creates inconsistent Elo benchmarks between machines.

### Distortion 2: Ultra-Shallow Depth Overfitting
- At `1+0.01`, search only reaches **depth 8–11** (~200k nodes per move).
- Optimizing purely for `1+0.01` can lead to "bullet overfitting": heavily favoring ultra-aggressive pruning that blunders at depth 15, while penalizing positional corrections that need depth to accumulate.

### 🛠️ The Solution: Fixed-Node Testing (`nodes <N>`)
When testing on laptops, battery power, or across different machines, replace wall-clock time with **Fixed-Node Testing**:
```bash
python tools/fastchess/run_elo_bench.py --baseline v1.0.6 --games 500 --nodes 50000 --concurrency 8
```
- **100% Hardware Independent:** Every single move searches the exact same number of nodes regardless of whether your laptop is throttled, plugged in, or running background apps.
- **Deterministic Evaluation:** Eliminates clock jitter, OS thread scheduling noise, and GC pauses from match results.
- **Direct Search Efficiency Measurement:** A stronger engine at fixed nodes is mathematically proven to have higher node quality (better pruning, better move ordering, and better evaluation).

---

## 🏷️ Feature Categorization Guide

Before launching a test, classify the candidate into its natural domain:

| Category | Typical Features | Natural Search Depth | Primary Validation Path |
| :--- | :--- | :---: | :--- |
| **Category A: Speed & Tactical Pruning** | Mailbox arrays, bitboard chains, flattened history, shallow SEE pruning, move picker stages | Depth 8–12 | Tier 1 Sanity &rarr; Tier 2 SPRT (1+0.01 or nodes 50k) |
| **Category B: Positional & Search Depth** | Pawn correction history, 2-fold repetition, singular extensions, endgame tablebases | Depth 13–20 | Tier 1 Sanity &rarr; Tier 3 MTC (3+0.03 or nodes 100k) |

---

## 💻 CLI Quick-Reference for Developers

```bash
# 1. Run full unit test suite (always required before testing)
dotnet test -c Release

# 2. Run a fast 250-game Tier 1 Sanity Check vs baseline
python tools/fastchess/run_elo_bench.py --baseline v1.0.6 --games 250 --tc 1+0.01 --concurrency 8

# 3. Run a deterministic Fixed-Node Sanity Check (laptop/battery safe)
python tools/fastchess/run_elo_bench.py --baseline v1.0.6 --games 250 --nodes 50000 --concurrency 8

# 4. Run an SPRT Validation Match (stops automatically when proven)
python tools/fastchess/run_elo_bench.py --baseline v1.0.6 --sprt 0.0 3.0 --max-games 8000 --tc 1+0.01 --concurrency 8

# 5. Run a Moderate Time Control (MTC) match for Category B features
python tools/fastchess/run_elo_bench.py --baseline v1.0.6 --games 500 --tc 3+0.03 --concurrency 6
```
