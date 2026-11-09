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

## 3. Phase 2: Future Roadmap (Staging & Pseudo-Legal Movegen)

1. **Dedicated Quiet Move Generation (`GenerateLegalQuiets`):**
   - Currently, if captures fail to produce a beta cutoff in `MovePicker`, `EnsureAllMovesGenerated` re-runs `GenerateLegalMoves` from scratch, regenerating all captures and performing linear scans to mark played captures.
   - Adding `GenerateLegalQuiets` will allow `MovePicker` to generate only quiet moves when needed, avoiding all redundant capture regeneration.
2. **Parallel Bitboard Pawn Generation:**
   - Replacing the iterative pawn-by-pawn loop with parallel bitboard shifts (`(unpinnedPawns << 8) & ~occupied & targetMask`, etc.) to eliminate rank and file conditional branches per pawn.
3. **Pseudo-Legal Move Generation:**
   - Transitioning from strictly legal move generation to pseudo-legal generation where moves are validated lazily when picked, bypassing pin and king safety checks on nodes that cut off on the TT move or first capture.

---

## 4. Benchmark Validation Record

| Benchmark Metric | Baseline (v1.0.0) | Phase 1 (NPS Optimized) | Delta / Equivalence |
| :--- | :--- | :--- | :--- |
| **Bench Total Nodes** | 411,233 | 411,233 | **100% Identical (0 divergence)** |
| **Startpos Perft(5)** | 4,865,609 | 4,865,609 | **100% Identical** |
| **Startpos Perft(6)** | 119,060,324 (~190M NPS) | 119,060,324 (232.5M NPS) | **+22.3% Throughput** |
| **Kiwipete Perft(4)** | 4,085,603 | 4,085,603 | **100% Identical** |
| **Kiwipete Perft(5)** | 193,690,690 (~220M NPS) | 193,690,690 (284.8M NPS) | **+29.4% Throughput** |
| **Unit Tests Passed** | 234 / 234 (100%) | 234 / 234 (100%) | **0 regressions** |
