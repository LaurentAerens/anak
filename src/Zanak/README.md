# Zanak Chess Engine & The ZAP Search Architecture

## 1. Executive Summary & Philosophy

**Zanak** is an experimental chess engine developed within the Anak repository, built from the ground up to validate an alternative to standard alpha-beta and Monte Carlo Tree Search (MCTS): **Zero-branching Accelerated Probe (ZAP)**.

Traditional chess engines construct explosive game trees using minimax, alpha-beta pruning, null-move pruning, late move reductions (LMR), and principal variation search (PVS). While highly refined over four decades, tree-based minimax search suffers from combinatorial branch explosion, high synchronization overhead across multi-core systems, and horizon effects.

**Zanak rejects minimax search entirely.** 
It has:
- **Zero alpha-beta minimax routines.**
- **Zero multi-ply branch trees.**
- **Zero move-list generation during spine probes.**

Instead, Zanak models chess search as **fast, linear, greedy trajectory probes** ("spines") combined with **prioritized positions** ("crosspoints"), where asynchronous agent squads promote or refute lines through localized 1-ply sibling checks. Optimism is intentional: agents challenge the greedy hypothesis rather than replacing it with a minimax tree.

---

## 2. Core Architecture: How ZAP Works

```
                        [Root Position]
                               |
            +------------------+------------------+
            | (Phase 1: All Legal Root Moves)     |
            v                                     v
       [Candidate 1]                         [Candidate N]
            |                                     |
    (Linear Greedy Spine)                 (Linear Greedy Spine)
            |                                     |
   Ply 1: Move A                         Ply 1: Move X
   Ply 2: Move B  <--- [Crosspoint]       Ply 2: Move Y
   Ply 3: Move C        | (Adversarial    Ply 3: Move Z
        ...             |  Refutation)         ...
   Ply D: Eval          v                 Ply D: Eval
                   (1-Ply Check &
                   Subtree Splice)
```

The ZAP search algorithm executes in four distinct phases:

### Phase 1: Full Root Expansion & Baseline Spine Generation
1. Generate all legal root moves for the side to move.
2. For each root move, trace a **purely greedy, linear trajectory** down to target depth (e.g. depth 8–14 depending on time control).
3. Trajectory steps are selected using `ZapPolicy.TryGetQuickMove`, a dedicated bitboard-level decision ladder that executes without allocating or generating full move lists.
4. Each step records:
   - `Move`: the selected action.
   - `StaticEval`: engine-perspective position evaluation.
   - `EvalDelta`: $\Delta\text{eval} = \text{eval}_{t} - \text{eval}_{t-1}$.
   - `ZobristKey`: position hash.

### Phase 2: Retrospective Discrepancy & Crosspoint Extraction
Every recorded trajectory position is eligible for agent work:
- **Shock Ranking (responsibility, not raw delta):** An eval jump usually shows up one ply *after* the real mistake. In `54, 53, 314 (knight taken), 354 (silent move)`, the capture at 314 is only the bill: the error is the move that allowed it, and the victim's reply that leaves the debt unpaid. `ZapWorkQueue.ComputeResponsibility` charges each large gain for the mover (above an adaptive gate of `AdaptiveShockBase + (ply-1)·AdaptiveShockSlope`) to the move that allowed it. While the debt is unpaid, it also charges the victim's following moves at a decaying weight (`CrosspointDebtDecayPct`). The capture itself is not blamed. Tablebase scores and immediate replies add to this; low responsibility does not exclude a position.
- **Tactical Ranking:** Checks, captures, promotions and exposed pieces increase pickup priority independently of delta. `ZapCrosspoint.GetMaterialSignal` also flags spine moves that skip a better capture (a missed recapture) or that leave material hanging to the opponent's best SEE capture. Both signals are heuristic scheduling hints, not legality or material-winning proofs.
- **Adversarial Opponent Reply (Ply 1):** Every root candidate automatically emits a Ply 1 crosspoint for the opponent's reply.
- **Crosspoints:** A `ZapCrosspoint` encapsulates the exact board state at ply $k$, its parent evaluation, shock magnitude, tactical priority, and remaining search horizon. Pickup ranking also favours higher-scoring spines and proximity to the root, with a penalty for previous pickups from linked spines. This is independent of physical storage order.

### Phase 3: Dual-Role Worker Exploration (Refutation & Promotion Squads)
A pool of asynchronous background threads pops crosspoints from a prioritized `ZapWorkQueue`:
- **Resumable batches:** `MaxSiblingExploration` (default 6) limits new sibling spines per pickup, not the total number of alternatives. The original continuation is traced once per work revision. Deferred siblings return to the queue; subsequent batches merge their children into the tree without deleting earlier siblings or their descendants. A longer horizon or back-credit revision waits for the current batch to release its lease before fresh work can start.
- **Refutation Squads (`isOpponentTurn == true`):**
  - Search for opponent sibling moves at ply $k$ that disprove the engine's line.
  - If any sibling achieves a worse score for the engine than the baseline spine, the line is **refuted**.
  - Retains a refutation ceiling: $\text{AgentCeiling} = \min(\text{AgentCeiling}, \text{RefutedScore})$.
- **Promotion Squads (`isOpponentTurn == false`):**
  - Explore alternative engine moves at ply $k$ to see if a superior continuation exists.
  - Records an improvement floor, capped by any existing refutation ceiling. Promotions cannot override refutations.
- **Subtree Splicing (`SpliceTrajectoryTail`):**
  - The original move and alternatives are traced at matching bounded horizons. A replacement includes the alternative junction move itself, then its continuation. Cache entries are move hints, not substitutes for complete trajectories.
  - Accepted replacement positions become eligible crosspoints themselves, without repeatedly extending the horizon. Stale generations or superseded junction moves cannot overwrite the current trajectory.
- **Back Credit (`CreditEarlierDecisions`):** A splice that moves a spine score by at least `BackCreditMinCp` changes the context for the other side's earlier decisions. For example, a refutation that ran while the spine scored 0 is stale once a promotion raises the score to +377. Positions at junction−1, −3, … (up to `BackCreditMaxSteps`, with decaying boost) are passed to `ZapWorkQueue.Reconsider`, which raises their pickup priority. Positions that were already explored are reopened up to `CrosspointMaxReopens` times. Back credit changes only scheduling; scores still change only through agent splices.

### Phase 4: Anytime Decision & Root Pruning
- Candidates are discarded for poor scores only when both the spine and an available independent screen are poor. A missing screen is not evidence against a candidate.
- The engine guarantees a valid `bestmove` at every millisecond via `_sharedState.GetBestMove()`.

---

## 3. Directory & File Structure

All Zanak source code lives under `src/Zanak/`:

```
src/Zanak/
├── Program.cs                     # Entry point for standalone zanak binary
├── Search/
│   ├── ZapCrosspoint.cs           # Crosspoint struct (board, ply, shock, kind, role)
│   ├── ZapParams.cs               # Search hyper-parameters and tunable margins
│   ├── ZapPolicy.cs               # Bitboard-first move ordering and quick-move ladder
│   ├── ZapProbe.cs                # Linear greedy spine tracer (zero minimax)
│   ├── ZapRootFilter.cs           # Root candidate management and deduplication
│   ├── ZapSearch.cs               # Main search orchestrator (Phase 1-4 pipeline)
│   ├── ZapSearchEngine.cs         # ISearchEngine bridge for Anak framework integration
│   ├── ZapSharedState.cs          # Thread-safe candidate state, ceilings/floors, and splicing
│   ├── ZapTrajectory.cs           # Trajectory step and history representation
│   ├── ZapTranspositionTable.cs   # Shared TT for verified refutations and probe steps
│   ├── ZapWorkerPool.cs           # Multi-threaded Refutation and Promotion workers
│   └── ZapWorkQueue.cs            # Priority queue for tactical crosspoint exploration
├── Time/
│   ├── SearchBudget.cs            # Search resource constraints
│   ├── TimeManager.cs             # Adaptive time allocation based on game phase
│   └── TimeParams.cs              # Time management hyper-parameters
└── Uci/
    ├── UciHandler.cs              # Full UCI protocol parser and dispatcher
    └── UciOutput.cs               # UCI telemetry emitter (info depth, nps, pv)
```

---

## 4. Key Components Explained

### 4.1. `ZapPolicy` (Move Selection Ladder)
To avoid the CPU cost of allocating and generating all legal moves along the spine, `ZapPolicy` evaluates moves through a strict bitboard priority cascade:
1. **In Check:** King evasions (safe squares, interpositions, or capturing the checking piece).
2. **Queen Captures and Promotions:** Take an enemy Queen (LVA), then try an immediate queen promotion before unrelated captures or retreats. Promotion candidates come directly from penultimate-rank pawn bitboards; captures precede pushes, and post-move safety rejects a promotion lost to an uncovered attack.
3. **Queen/Rook Threats:** Evade own attacked Queen, then take enemy Rook (LVA) or evade own attacked Rook. A rook counts as threatened when it is undefended or attacked by a piece at least 100cp cheaper, so a defended rook attacked by a bishop is still treated as threatened.
4. **Minor Piece Threats:** Take enemy Bishop/Knight or evade attacked minor pieces.
5. **Pawn Captures:** Take a pawn using the existing LVA/SEE rule.
6. **Checks:** Deliver forcing checks with positive or neutral Static Exchange Evaluation (SEE). Pawn-check targets use the reverse attack mask of the moving color, so checking pushes precede unrelated quiet pawn pushes for either side.
7. **Pawn Evasions and Advances:** Evade attacked pawns or advance. Reject destinations where a legal enemy capture wins material according to SEE. Safety uses post-move occupancy, including newly opened sliding attacks; defended, non-losing exchanges remain allowed.
8. **Legal Fallback:** Prefer a legal move with a non-losing destination. If none exists, retain a legal fallback rather than stop the spine.

Piece evasions keep their existing destination order but validate safety after moving, without falling back blindly to an attacked square. If no sound escape exists, the policy also tries capturing an attacker with another piece, provided that removes the threat. These are local bitboard/SEE rules, not a multi-ply search.

The work queue gives the **first batch** of eligible Ply 1 adversarial refutations strict precedence over deeper crosspoints and promotions. After one batch, unfinished siblings return to ordinary ranking so quiet root alternatives cannot monopolize pickup ahead of a deeper tactical discrepancy. Remaining siblings stay eligible; a deeper revision starts a fresh first batch. There is no parent-versus-leaf dormant gate. Only stale generations are excluded. Spines are never pruned: pruning happens only in the root filter, before spine creation. A demoted, refuted or dead spine keeps all its crosspoints, and its lower score just ranks them lower for pickup. Its traces also run to their full horizon. Shock, tactical features, score, proximity, and diversity ordering applies within each priority class. A shared pickup increments the counts of all linked spines.

Material scheduling signals use capture-only generation outside check and full legal evasions in check. This avoids generating unrelated quiet moves while retaining the same missed-capture/hanging-piece signal; it does not change crosspoint eligibility.

Within each class, pickup also subtracts estimated next-batch cost (`remaining horizon * next batch size * PickupCostWeight`, default weight 2) and a per-completed-batch diversity penalty. Set `PickupCostWeight=0` to disable the cost term for comparisons. Unknown sibling counts initially use the configured batch size. These are scheduling estimates, not measured CPU time or proofs of move quality. Agent siblings are ordered by TT hint, promotion, captures/SEE and checks; this does not introduce candidate evaluation into the single-move greedy spine policy.

`InterleaveRootSeeding` is an experimental UCI check option, **false by default**. When enabled, the caller processes at most one queued batch after publishing each root spine, before seeding the next root. Background agents remain parked during this phase, so a partially initialized root cannot be observed by a worker. Seeding and agent work alternate rather than racing, and each batch yields back to root coverage. The usual worker pool starts after seeding. Compare both modes; interleaving can delay full root coverage and has not been demonstrated to improve Elo.

Short-clock validation (2026-11-13) keeps this option **off**: 1,080 warmed, untraced searches covered 30 positions, three repeats, both modes, and 2/5/10/25/50/100 ms clocks at horizon 8, Hash=64 and Threads=1 (worker plus caller). At 5 ms, mean initial-spine coverage of roots surviving the unchanged pre-filter was **99.46% without interleaving versus 35.57% with it**. Anak's 50,000-node preferred root received a spine in 89/90 versus 35/90 searches; there were 54 paired misses caused by interleaving and none in the other direction. At 10 ms there was one further paired miss; at 25-100 ms both modes covered every eligible root in this suite. At 2 ms both modes exhausted the clock during setup. These are machine-specific coverage diagnostics, not strength estimates or proof that Anak's preferred move is uniquely best.

An earlier 250-game `1+0.01` comparison scored 53.4% for interleaving, but its estimated +23.66 +/- 35.88 Elo was inconclusive. The short-clock coverage failure blocks unconditional default activation; the longer SPRT was not started. A future budget-aware variant needs its own coverage and playing-strength validation rather than assuming this result transfers to it.

With no fixed depth limit, the search is time-driven. Spine horizons deepen up to 32 plies, both when the queue runs low and on a time schedule, and at that cap every candidate is re-deepened in score order. Every agent-traced continuation becomes a subspine in a spine tree (`ZapSpineTree`), and each subspine position becomes a new crosspoint. Values back up through the tree one picked crosspoint at a time, so the queue practically never runs empty and the time limit ends the search. The queue is capped at `MaxQueueEntries`. UCI `go depth N` replaces the clock's depth choice: spines are N plies, the horizon does not grow with time, and the time limits are unchanged. Before `bestmove`, Zanak emits `info string zap exit <reason> drain <reason> depth … time used/soft agents … improved … spliced … entries … subspine-positions … tree-nodes … capped … pending …`. This shows why the search stopped, how many agents found a better sibling, how many tree expansions changed a root value, and how large the tree and queue grew.

### 4.2. `ZapProbe` (The Linear Tracer)
`ZapProbe.Trace` is the computational engine of ZAP:
- Takes a starting `Board` and target depth $D$.
- In a tight loop, requests the next step from `ZapPolicy`.
- Applies the move incrementally, updating NNUE accumulators.
- Probes Syzygy tablebases (if piece count $\le 5$ and no castling rights remain).
- Evaluates the position and appends a `ZapTrajectoryStep`.
- Terminates upon reaching depth $D$, insufficient material, repetition draw, or tablebase hit.

### 4.3. `ZapSharedState` & Asymmetric Bounds
ZAP keeps **asymmetric candidate score bounds**:
- `AgentCeiling`: The lowest retained opponent-refutation estimate. Neither promotion nor baseline refresh can raise the score above it.
- `AgentFloor`: A retained engine-improvement estimate, never above the ceiling.

These are trajectory bookkeeping bounds, not game-theoretic proofs about all possible replies. Workers must finish before the next search changes the root or evaluator. Stable-dominance checks count actual increases in the leader's recorded trajectory depth, not repeated polling.

### 4.4. NNUE Activation and Diagnostics

- Production search requires an active shared NNUE; it no longer silently substitutes the bare heuristic evaluator. An evaluator can still be explicitly injected for diagnostics.
- UCI advertises and honours `NnuePath`, reports the active directory and root expert, and refuses search after a failed network configuration until a valid configuration is supplied.
- An explicit `NnuePath` requires all six valid full expert files; corrupt files cannot silently select embedded replacements. This intentionally makes strength-run overrides stricter than the shared loader's partial-directory fallback. The default embedded/discovered model configuration remains available.
- NNUE workers share immutable weights, not accumulator stacks. Other injected evaluators are retained rather than replaced by the heuristic.
- NNUE parity tests require active models and compare master, worker and full-refresh evaluations, including both colours, castling, en passant and promotion.
- `ZapSharedState.AgentTraced` optionally reports agent baselines, alternatives, completion and applied-root counts with their trajectories. `ZapWorkQueue.Snapshot()` distinguishes eligible/picked positions; picked does not mean completed. These diagnostics allocate trace arrays only when subscribed.
- `ZapWorkQueue.Picked` reports the selected root, role, ply, exact priority components, estimated cost and sibling progress. `ZapWorkerPool.BatchCompleted` reports explored/deferred siblings, whether the crosspoint is complete, batch duration and applied-root count. Partial batches are never reported as full coverage.
- The optional `PickupTraceFile` UCI setting writes `start`, `pickup`, `batch` and `summary` JSONL records without allocating full trajectory traces. It is disabled by default; set it to `<empty>` to disable it again. The file is overwritten per search and closed after workers stop. Use an existing parent directory and separate files for separate searches. Logging still adds overhead: leave both trace options disabled in latency and strength runs.
- Final `zap` diagnostics append root-filter milliseconds, seeded-root count, seeding milliseconds, first published batch milliseconds (`-1` if none), completed crosspoints and traced sibling count. Seeding time includes interleaved batches when enabled; it is not pure spine-tracing time. Root filtering and batch timings use a monotonic clock. The search caller normally helps the worker pool, so UCI `Threads=1` is not a guarantee of single-thread execution unless search-move tracing selects caller-only mode.
- The search clock includes queue/tree/TT setup and clearing, not just spine generation and exploration. Expired or cancelled searches return a legal fallback before starting more work. Timing checks remain cooperative; an individual filter, probe, tablebase call or GC pause can still overrun a very small clock.
- The optional `SearchTraceFile` UCI setting records visited search-move edges as JSONL for the [Search Movie tool](../../tools/search_movie/README.md), with monotonic per-visit `seconds` and a final `elapsed_seconds` record for shared-clock playback. It is disabled by default; `--all-lines` enables it for a board-free, cumulative replay of root seeds and agent continuations in visit order. With tracing and `Threads=1`, agents run serially on the search caller so probes cannot interleave with a background worker. Normal untraced scheduling is unchanged. Tracing adds diagnostic overhead and should not be used for strength or throughput comparisons.

### 4.5. Offline Policy Calibration

The [Anak teacher annotation tool](../../tools/zanak_policy/README.md) labels FEN/EPD positions with Anak's best move, score and PV using a persistent UCI process. Output is resumable JSONL, with explicit input/teacher errors. Use a diverse held-out corpus including positions from optimistic Zanak spines, and measure harmful policy deviations and downstream repair cost rather than maximizing best-move agreement.

This is offline tooling only. The runtime policy still makes one direct rules/bitboard move-selection call per spine step: no NNUE-evaluated move shortlist and no mini-minimax. A 100,000-position corpus and calibrated rule changes are future experiments, not generated results.

---

## 5. Known Limitations & Active Research

As demonstrated in recent benchmark runs against Stockfish 1320:
1. **Local Safety Is Not Tactical Proof:**
   - Pawn pushes and piece evasions now use post-move destination safety, but SEE does not prove that the entire position is safe or compare positional continuations.
   - If no sound piece escape or attacker capture exists, defensive interpositions and other ways to save a threatened piece remain an area for further policy work.
2. **Ply 1 Latency and Score Consistency:**
   - Immediate opponent refutations have strict queue priority. Background workers start after root seeding; optional caller-only interleaving can challenge roots earlier. A 5 ms refutation deadline is not guaranteed.
   - Agent comparisons now use matching continuation horizons and complete replacement trajectories. Broader strength, fairness under strict Ply 1 priority, and the quality of retained score bounds still require measurement; a global score-perspective sign bug has not been established.
3. **Throughput vs. Horizon:**
   - ZAP requires high node throughput on single cores. Bitboard intrinsics (`BMI2`, `AVX2`) and vectorized SEE are critical to keeping spine tracing sub-microsecond.
