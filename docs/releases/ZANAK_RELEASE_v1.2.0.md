# Zanak `v1.2.0` — Phase-Aware Policy, Cross-Move TT Persistence & `TimeManager` Depth Control

`zanak-v1.2.0` is a minor release for **Zanak** (ZAP — Zero-branching Accelerated Probe) focused on closing the middlegame/endgame gap in blitz and longer time controls while preserving Zanak's zero-minimax, linear greedy spine architecture and bullet strength.

---

## Highlights in `v1.2.0`

* **Phase-Aware Bitboard Policy Rules (`ZapPolicy`):**
  * **Opening / Middlegame (`board.Queens != 0`):** Added O(1) bitboard rules before Step 25 fallback for safe castling (`TryGetSafeCastling`), safe home-rank minor piece development to `c3..f6` (`TryGetSafeMinorDevelopment`), and safe central `d`/`e` pawn advances (`TryGetSafeCentralPawnPush`).
  * **Endgame (`board.Queens == 0`):** Added safe passed-pawn advances across early ranks (`TryGetSafePawnPush` using `ZapTables.GetPawnForwardSpanMask`) and king activation toward the center and pawns (`TryGetSafeEndgameKingMove`).
  * **Piece Defense in Evasions (`TryGetSafePieceDefense`):** When an undefended threatened piece cannot escape, interpose, or be defended by a pawn push, `TryEvadePiece` now checks safe Knight/Bishop/Rook/King defense squares via bitboard lookups.
* **Cross-Move TT Persistence (`ZapSearch`):**
  * Removed per-move `_tt.Clear()` in `SearchCore` so `_tt.NewSearch()` ages entries naturally and agent-verified `flags == 2` moves persist across consecutive turns (`ucinewgame` / `Clear()` still wipes the table).
* **`TimeManager` as Single Source of Truth (`TimeManager`, `TimeParams`, `ZapSearch`, `UciHandler`):**
  * Removed the hardcoded `threadScaledCap` override in `ZapSearch` so `TimeManager.AllocateBudget` controls `MaxDepthHorizon` directly (while explicit UCI `go depth N` continues to override clock-derived depth).
  * Exposed the `log10(ms * threads)` curve parameters (`LogBaseDepthOffsetPct`, `LogBaseDepthScalePct`, `LogHorizonDeltaOffsetPct`, `LogHorizonDeltaScalePct`) and an optional low-time modifier (`LowTimeDepthBoostPct`, `LowTimePivotMs`, default `0`) as `TimeParams` / UCI options.
* **Multi-TC SPSA Tooling (`tools/tuning/spsa.py`):**
  * Synced `ZANAK_TIME_PARAMETERS` and `ZANAK_SEARCH_PARAMETERS` defaults to `v1.1.0`+, added a `zanak_curve` batch, and added comma-separated `--tc` cycling across time controls.

---

## Verification & Benchmarks (`Threads=1`, `Hash=64`, `openings.epd`)

* **Unit Tests:** `dotnet test -c Release` — **414 / 414 passed** (`0` failures, `0` skipped).
* **Python Tests:** `pytest` — **20 / 20 passed**.
* **Matches vs `Stockfish-1320` (`UCI_LimitStrength=true`, `UCI_Elo=1320`, `Threads=1`):**

| Time Control | Games | Score | W-L-D | Elo vs SF-1320 | Est. Elo | LOS |
|---|---:|---:|---:|---:|---:|---:|
| `1+0.01` | 400 | `56.50%` | `220-168-12` | `+45.42 ± 33.92` | **~1349 / ~1365** | `99.39%` |
| `5+0.05` | 100 | `50.00%` | `50-50-0` | `0.00 ± 68.99` | **~1327 / ~1320** | `50.00%` |
