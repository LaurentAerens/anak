# Anak Engineering & AI Agent Rules

This file defines the mandatory operational constraints, benchmarking standards, and evaluation protocols for all AI agents working on the Anak Chess Engine repository.

---

## 1. Mandatory Operational Constraints

### Git Operations
- **DO NOT perform any state-modifying Git operations without explicit user approval.**
- Specifically, the agent MUST NOT run or request execution of:
  - `git commit`
  - `git checkout` / `git switch` (changing branches)
  - `git branch` (creating or deleting branches)
  - `git push`
  - `git merge` / `git rebase`
  - `git reset` / `git restore`
  - `git stash`
- Read-only queries (`git status`, `git diff`, `git log`) are permitted at any time.

### Automated Testing
- **ALWAYS run all automated unit tests (`dotnet test -c Release`) to validate changes.**
- Never consider any task complete unless 100% of unit tests pass with zero errors or regressions.

---

## 2. Benchmarking & Elo Evaluation Protocol

To balance **consistency, compute budget, and real-life playing strength**, optimization candidates follow specialized validation tracks depending on the nature of the change:

### Streamlined Single-Run Validation (Search & Positional Features)
Instead of waiting through separate 250-game gate matches followed by 10,000-game matches, validation uses **Early-Stopping SPRT**:
```bash
python tools/fastchess/run_elo_bench.py --baseline <active_baseline> --sprt 0.0 3.0 --max-games 8000 --tc 1+0.01 --concurrency 8 --early-stop
```
- **Severe Regression / Broken Logic:** The `--early-stop` guard checks confidence intervals continuously and terminates failed candidates early (typically in $400 - 600$ games).
- **Early Winner Acceptance:** SPRT confirms statistically significant $+3.0\text{ Elo}$ improvements with $95\%$ confidence early (typically in $1,500 - 2,500$ games).
- **Aborts Flop Features:** Neutral/flat candidates stop before wasting hours of compute.

---

## 3. Pure NPS / Speedup Validation Protocol (Category A)

For code changes targeting **pure speedup with identical search logic** (e.g., bit manipulation, data structure flattening, inlining, SIMD vectorization, cache locality, memory layout):

1. **Unit Test Gate:** 100% of unit tests pass (`dotnet test -c Release`).
2. **Fixed-Node Determinism Check:** Verify across standard benchmark positions that fixed-node searches produce **100% identical best moves and node counts** to the baseline:
   - Must match the exact node count of the baseline on canonical positions (`anak bench <depth>`).
   - Confirms that the change did not accidentally alter move ordering, pruning logic, or evaluation semantics.
3. **NPS Throughput Gate:** Run benchmark harness (`anak bench 12`) to verify statistically significant NPS speedup and **0 GC allocations** across Gen 0, Gen 1, and Gen 2.
4. **Fast-Track Acceptance:** Pure NPS improvements that maintain 100% deterministic search trees and yield demonstrable NPS speedup without modifying search semantics are accepted directly or verified via a short fast confirmation run without requiring full 8,000-game match cycles.

---

## 4. Hardware Independence (Fixed-Node Alternative)
When testing on laptops, battery power, or across different machines (Main PC vs. Laptop), wall-clock time controls (`1+0.01`) suffer from thermal throttling and clock jitter. In these scenarios, use **Fixed-Node Testing** (`--nodes 50000` or `--nodes 100000`):
- 100% deterministic search trees.
- Zero variance from CPU throttling, battery drain, or OS background activity.
