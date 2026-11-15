# Zanak v0.0.1 — First Milestone Release (ZAP Architecture)

**Zanak** is an experimental chess engine built in the Anak repository to validate **ZAP (Zero-branching Accelerated Probe)**—an alternative search paradigm that replaces traditional minimax/alpha-beta trees and MCTS with linear greedy trajectories and asynchronous worker refutation.

![Zanak ZAP Search Demo](https://raw.githubusercontent.com/laerens/anak/zanak-v0.0.1/docs/zanak-v0.0.1-demo.gif)

---

## What is ZAP (Zero-branching Accelerated Probe)?

Traditional engines build exponentially branching game trees via minimax, alpha-beta pruning, LMR, and PVS. **Zanak has zero minimax routines and zero multi-ply branch trees.** Instead, it models search in four phases:

1. **Phase 1 — Root Expansion & Linear Greedy Spines (`ZapProbe` + `ZapPolicy`)**
   * Generates all legal root moves and traces a single, fast, linear greedy trajectory ("spine") for each candidate using `ZapPolicy.TryGetQuickMove`—a 25-step bitboard-first decision cascade (checks, free/winning captures, piece evasions, simplifying trades when ahead, safe checks, safe pawn pushes) with zero move-list allocation along the spine.
2. **Phase 2 — Retrospective Discrepancy & Crosspoint Extraction (`ZapWorkQueue`)**
   * Evaluates `Δeval` along each spine to assign **blame/responsibility** to the moves that allowed tactical swings, rather than the capture that collected them.
   * Extracts prioritized `ZapCrosspoint` work items, giving immediate root-proximity priority to `Ply 1` and `Ply 2` replies.
3. **Phase 3 — Dual-Role Worker Squads & Subtree Splicing (`ZapWorkerPool` + `ZapSharedState`)**
   * **Refutation Squads** (opponent turns) search for sibling replies that disprove optimistic lines and clamp the candidate's `AgentCeiling`.
   * **Promotion Squads** (engine turns) look for stronger continuations, bounded by any refutation ceiling.
   * Accepted improvements splice directly into the spine (`SpliceTrajectoryTail`) and trigger **Back Credit** (`CreditEarlierDecisions`) to reopen earlier junctions whose context changed.
4. **Phase 4 — Anytime Decision (`ZapSharedState`)**
   * Guarantees a valid `bestmove` at any millisecond, using multi-tier NNUE evaluation (`ZanakNnueEvaluator`) and Syzygy tablebase probing.

---

## Highlights in `v0.0.1`

* **Smooth Logarithmic Depth Scaling (`TimeManager`):** Replaced discrete step buckets with a continuous `log10(softMs)` depth curve that scales smoothly all the way down to `depth 1` (pure 1-ply evaluation without spine generation) in extreme bullet time pressure.
* **Near-Root Crosspoint Priority (`ZapWorkQueue`):** Added root-proximity priority boosts for `Ply 1` and `Ply 2` crosspoints so tactical refutations near the root are explored ahead of deep zero-shock work.
* **Winning-Position Simplification (`ZapPolicy`):** Added `TryGetSimplifyingTrade` to prefer `SEE >= 0` piece/queen trades when materially ahead (`>= +200 cp`), reducing opponent counterplay in won positions.
* **Clean UCI Telemetry (`ZapSearch`):** Fixed `info depth 0` emissions and added `info depth 1` reporting on forced single-move fast-paths.

---

## Verification & Benchmark

* **Unit Tests:** `dotnet test -c Release` — **399 / 399 passed** (`0` failures, `0` skipped).
* **50-Game Benchmark vs `MinimalChess-0.2` (`tc=1+0.01`, `openings.epd`):**
  * **Overall Score:** `29.0 / 50` (**58.0%**, `+28 -20 =2`)
  * **Elo Diff:** **+56.07 ± 105.56** (LOS: **86.31%**)
  * **Non-Forfeit Score:** `23.0 / 44` (**52.3%**, up from `44.2%` baseline)
