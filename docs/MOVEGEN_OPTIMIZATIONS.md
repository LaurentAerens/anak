# Move Generation Optimization Architecture & Roadmap

This document outlines the optimization roadmap and benchmarks for the move generation department in the **Anak Chess Engine**.

---

## 1. Overview of Anak Move Generation

Anak uses a bitboard-based legal move generator located in `src/Anak.Core/MoveGen/MoveGenerator.cs`:
- **Generation Strategy:** Strictly legal move generation. All generated moves are guaranteed to be fully legal (pins, king checks, and en-passant discovered checks are validated upfront).
- **Search Staging:** Integrated with `src/Anak.Search/Minimax/MovePicker.cs`, which generates captures first for tactical cutoff efficiency, followed by quiet moves when needed.

---

## 2. Phase 1: Pure NPS Optimizations (100% Zero-Eval & Node-Equivalence)

The Phase 1 optimizations focus entirely on raw NPS throughput without altering move ordering, search trees, or evaluations. Every move is generated in identical order with zero regression.

### 2.1 Elimination of `stackalloc ulong[64] pinRays` (512-Byte Stack Zeroing)
- **Previous Implementation:** Both `GenerateLegalMoves` and `GenerateLegalCaptures` allocated a 64-element `ulong` span on the stack (`Span<ulong> pinRays = stackalloc ulong[64];`). The .NET runtime automatically zero-initializes this 512-byte buffer on every single move generation invocation.
- **Optimization:** If a piece at `from` is pinned to `kingSq`, the king, the pinned piece, and the pinner are collinear on the same rank, file, or diagonal. Thus, `Bitboard.Line[(int)kingSq, (int)from]` contains the exact infinite ray connecting the king and pinner.
- **Benefit:** Completely eliminates the 512-byte stack allocation and zero-initialization overhead. Pin rays are retrieved on-demand via the precomputed `Bitboard.Line` 1D table with zero heap or stack allocation.

### 2.2 Precomputed Enemy King & Pawn Danger Masks
- **Previous Implementation:** For every candidate king move (up to 8 squares), `IsSquareAttackedCustom` performed 5 individual checks: pawn attacks, knight attacks, king attacks, bishop attacks, and rook attacks.
- **Optimization:** Enemy pawns and the enemy king cannot change their attack rays based on whether our king occupies the square. We precompute an `unsafeForKing` bitmask once:
  $$\text{unsafeForKing} = \text{enemyPawnAttacks} \mid \text{KingAttacks}[\text{enemyKingSq}]$$
  Filtering `kingTargets &= ~unsafeForKing;` eliminates impossible king target squares with a single bitwise operation before calling slider attack lookups.

### 2.3 Early-Outs for Missing Enemy Sliders
- **Previous Implementation:** Checkers and pinners detection called `MagicBitboards.GetBishopAttacks` and `GetRookAttacks` unconditionally.
- **Optimization:** If `(board.Bishops | board.Queens) & enemyPieces == 0` or `(board.Rooks | board.Queens) & enemyPieces == 0`, bishop/rook attack queries are bypassed. In endgames and pawn endings, magic bitboard index computations are completely avoided.

### 2.4 Optimized Slider Checker Type Detection
- **Previous Implementation:** When in single check, determining whether the checker was a slider required a mailbox lookup (`board.GetPieceAt(checkerSq).Type()`).
- **Optimization:** Checks `(enemyBq | enemyRq) & checkers != 0` directly on bitboards to determine slider push masks, avoiding mailbox dereferencing and enum branching.

---

## 3. Phase 2: Staging & MovePicker Laziness (100% Zero-Eval & Node-Equivalence)

### 3.1 Lazy Move Generation for Killers & Countermoves in `MovePicker`
- **Previous Implementation:** When testing `Killer0`, `Killer1`, or `CounterMove`, `MovePicker` unconditionally called `EnsureAllMovesGenerated()`. This wiped the move list, re-generated all 35+ legal moves from scratch, and performed linear searches across all previously played captures. Since killer moves produce immediate beta cutoffs in 20–30% of non-capture nodes, this full move generation was entirely wasted work.
- **Optimization:** Tested killer and countermove legality lazily using `_board.IsLegal(move)` ($O(1)$ ~10ns check). If the killer causes a beta cutoff, full legal move generation is completely bypassed. If the killers fail to cut off, `EnsureAllMovesGenerated()` is called lazily and marks the played killers as picked.
- **Validation:** Total search nodes on `bench` remained **100% identical at 411,233 nodes**. Search time dropped significantly.

### 3.2 Branchless Precomputed Pawn Attack Lookups
- **Previous Implementation:** For every pawn, pawn captures branched on `file > 0`, `file < 7`, and individual West/East capture bitmasks.
- **Optimization:** Replaced the 4 branches per pawn with direct lookup into precomputed tables `Bitboard.WhitePawnAttacks[(int)from]` and `Bitboard.BlackPawnAttacks[(int)from]`, masked by `enemyPieces & allowedMask`. For the majority of pawns that have no captures, it exits with zero branches.
- **Validation:** 100% identical move generation; all unit tests and perft counts pass.

### 3.3 Streamlined `IsSquareAttackedCustom`
- **Previous Implementation:** `IsSquareAttackedCustom` received 10 parameters and tested pawns, knights, kings, bishops, and rooks on every king candidate square.
- **Optimization:** Because `kingTargets` is already masked upfront with `~unsafeForKing` (enemy pawn and king attacks), pawn and king checks inside `IsSquareAttackedCustom` were dead code. Pruned to 5 parameters testing only knights and sliders when present.

### 3.4 Dedicated `GenerateLegalQuiets` and Partition Invariant
- **Implementation:** Added `MoveGenerator.GenerateLegalQuiets(in Board, ref MoveList)` which generates only quiet moves (`quietTargetMask = inCheck ? pushMask : ~occupied`). In single check against a non-slider (pawn/knight check), it returns immediately since non-king quiet moves cannot block.
- **Validation:** Added xUnit theory unit tests verifying that across all test positions:
  $$\text{GenerateLegalMoves} \equiv \text{GenerateLegalCaptures} \cup \text{GenerateLegalQuiets}$$
  with zero overlap and zero omitted moves.

### 3.5 Incremental Quiet Appending in `MovePicker`
- **Previous Implementation:** When search progressed past killers to quiet moves (`ContinuationMoves` / `QuietMoves`), `MovePicker` cleared `_legalMoves`, re-generated all legal moves from scratch, and re-ran `ScoreCaptures()` with repeated SEE calculations during `BadCaptures`.
- **Optimization:** If captures are already generated (`_capturesGenerated == true`), `MovePicker` appends quiets via `GenerateLegalQuiets` starting at `_quietStart = _legalMoves.Count`. Good capture scores, picked masks, and bad capture indices remain intact; `ScoreCaptures()` is never re-run, and quiet scoring only scans from `_quietStart`.
- **Validation:** Bench node count strictly preserved at **411,233**.

### 3.6 Ray-Directional Queen Attacks in `Board.IsLegal`
- **Previous Implementation:** When validating queen moves, `Board.IsLegal` unconditionally computed both `GetBishopAttacks` and `GetRookAttacks`.
- **Optimization:** Evaluates orthogonality `(from.Rank() == to.Rank() || from.File() == to.File())` to perform only the single relevant slider lookup (`GetRookAttacks` for orthogonal rays, `GetBishopAttacks` for diagonals), cutting queen validation cost in half.

### 3.7 Hardware Intrinsic `BitOperations.IsPow2` for Pin Detection
- **Previous Implementation:** Pinned piece detection tested `blockers != 0 && (blockers & (blockers - 1)) == 0 && (blockers & ourPieces) != 0`.
- **Optimization:** Replaced the multi-branch condition with the single hardware intrinsic `BitOperations.IsPow2(blockers)` across all movegen methods.

---

## 4. Benchmark Validation Record

| Benchmark Metric | Baseline (v1.0.0) | Phase 1 (NPS Optimized) | Phase 2 (Lazy Staged Movegen) | Delta / Equivalence |
| :--- | :--- | :--- | :--- | :--- |
| **Bench Total Nodes** | 411,233 | 411,233 | **411,233** | **100% Identical (0 divergence)** |
| **Startpos Perft(5)** | 4,865,609 | 4,865,609 | **4,865,609** | **100% Identical** |
| **Startpos Perft(6)** | 119,060,324 (~190M NPS) | 119,060,324 (232.5M NPS) | **119,060,324** | **+22.3% Throughput** |
| **Kiwipete Perft(4)** | 4,085,603 | 4,085,603 | **4,085,603** | **100% Identical** |
| **Kiwipete Perft(5)** | 193,690,690 (~220M NPS) | 193,690,690 (284.8M NPS) | **193,690,690** | **+29.4% Throughput** |
| **Unit Tests Passed** | 234 / 234 (100%) | 234 / 234 (100%) | **242 / 242 (100%)** | **0 regressions, +8 tests** |
