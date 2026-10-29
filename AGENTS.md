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

To balance **consistency, compute budget, and real-life playing strength**, all optimization candidates must adhere to the **3-Tier Testing Pyramid**.

### Tier 1: Quick Sanity Gate (250 Games)
- **Time Control:** `1+0.01` or `nodes 50000`
- **Purpose:** Fast filter to catch broken logic, tactical blunders, or heavy regressions in under 4 minutes.
- **Decision Rules:**
  - **Hard Regression:** Score $< 47.0\%$ ($< -20\text{ Elo}$, LOS $< 15\%$) &rarr; **Reject & Revert immediately**.
  - **Neutral / Flat:** Score $47.0\% - 52.0\%$ (within $\pm 15\text{ Elo}$) &rarr; **Do NOT auto-reject**. Review domain category:
    - If Category A (tactical/speed): May abort if no speedup detected.
    - If Category B (positional/eval/depth): Graduate to Tier 2 SPRT or Tier 3 MTC.
  - **Positive:** Score $> 52.0\%$ &rarr; **Graduate to Tier 2 Validation**.

### Tier 2: Validation via SPRT (Sequential Probability Ratio Test)
- **Engine Command:**
  ```bash
  python tools/fastchess/run_elo_bench.py --baseline <active_baseline> --sprt 0.0 3.0 --max-games 8000 --tc 1+0.01 --concurrency 8
  ```
- **Stopping Conditions:**
  - **Pass (H1 accepted):** Candidate is statistically proven $+3.0\text{ Elo}$ better with $95\%$ confidence. Match terminates early (typically in $1,500 - 2,500$ games). **Accept feature.**
  - **Fail (H0 accepted):** Candidate fails to beat baseline. Match terminates early ($800 - 1,500$ games). **Reject & revert.**
  - Cuts compute time and battery usage by **60–70%** compared to blind 10,000-game matches.

### Tier 3: Moderate Time Control (MTC) Gate for Deep/Positional Features
- **Scope:** Reserved for Category B features (pawn/correction history, repetition logic, singular extensions, endgame tablebases) that require moderate search depth ($\text{depth} \ge 12$).
- **Time Control:** `3+0.03` or `5+0.05` (500–1,000 games) or fixed nodes (`nodes 100000`).

---

## 3. Feature Categorization Guide

- **Category A (Speed & Move Ordering):** Mailbox arrays, bitboard chains, flattened history tables, shallow SEE pruning, move picker sort order.
  - *Primary Validator:* Tier 1 Sanity &rarr; Tier 2 STC SPRT.
- **Category B (Positional & Search Depth):** Correction history, repetition detection, singular extensions, endgame tablebases.
  - *Primary Validator:* Tier 1 Sanity &rarr; Tier 3 MTC (deeper search) or Tier 2 SPRT.

---

## 4. Hardware Independence (Fixed-Node Alternative)
When testing on laptops, battery power, or across different machines (Main PC vs. Laptop), wall-clock time controls (`1+0.01`) suffer from thermal throttling and clock jitter. In these scenarios, use **Fixed-Node Testing** (`--nodes 50000` or `--nodes 100000`):
- 100% deterministic search trees.
- Zero variance from CPU throttling, battery drain, or OS background activity.
