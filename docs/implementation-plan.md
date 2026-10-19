# "Anak" Chess Engine — Architectural Implementation Plan (.NET 11 / C# 14-15)

## 1. Executive Architecture Overview
"Anak" is an ultra-high-performance chess engine architected for the `.NET 11` runtime (`net11.0`) leveraging modern hardware intrinsics (`Bmi2`, `Popcnt`, `BitOperations`), zero-GC allocation discipline during search and move generation, Magic Bitboards, Monte Carlo Tree Search (MCTS) with PUCT and Transposition Table DAG reuse, Syzygy endgame tablebase support, PolyGlot opening book reader, and strict UCI/SPRT compliance.

```
+-------------------------------------------------------------------------------+
|                                  Anak.Protocol                                |
|  - UCI Loop (async stdin/stdout)   - Bench & Diagnostic CLI                   |
|  - Time Management                 - SPRT & Tournament Protocol Compliance     |
+-------------------+-----------------------------------+-----------------------+
                    |                                   |
                    v                                   v
+-----------------------------------+   +---------------------------------------+
|            Anak.Search            |   |               Anak.Data               |
|  - Zero-GC MCTS Node Arena        |   |  - PolyGlot (.bin) Book Reader        |
|  - PUCT Selection & Expansion     |   |  - Syzygy (EGTB) WDL & DTZ Probing    |
|  - 64-Byte Cacheline TT           |   |  - Heuristic / Material Evaluator     |
|  - DAG Transposition Subtree Reuse|   |  - Pluggable IEvaluator Pipeline      |
+-------------------+---------------+   +-------------------+-------------------+
                    |                                       |
                    +-------------------+-------------------+
                                        |
                                        v
+-------------------------------------------------------------------------------+
|                                   Anak.Core                                   |
|  - 64-bit Bitboards & Little-Endian Rank-File Coordinates                    |
|  - Plain/Fancy Magic Bitboards + PEXT HW Intrinsics (Bishops, Rooks, Queens)   |
|  - Fast Pseudo-Legal & Legal Move Generation (zero heap allocations)          |
|  - 16-bit Compact Move Struct & InlineArray MoveBuffer                        |
|  - 64-bit Zobrist Hash Keys & Rolling History (Threefold Repetition, 50-move) |
|  - FEN Parser & Serializer, Terminal Game State Detection                     |
+-------------------------------------------------------------------------------+
                                        ^
                                        |
+-------------------------------------------------------------------------------+
|                                  Anak.Tests                                   |
|  - Perft Test Suite (Initial, Kiwipete, Positions 3, 4, 5)                    |
|  - Rule Edge Case Verification (Castling, En Passant discovered checks, Pins) |
|  - Zero-Allocation Regression Assertions                                      |
+-------------------------------------------------------------------------------+
```

---

## 2. Solution & Project Hierarchy

```
anak/
├── .editorconfig
├── .gitattributes
├── .gitignore
├── LICENSE (GPLv3)
├── README.md
├── Anak.sln
├── Directory.Build.props
├── .github/
│   ├── dependabot.yml
│   └── workflows/
│       ├── ci.yml
│       └── release.yml
├── src/
│   ├── Anak.Core/
│   │   ├── Anak.Core.csproj
│   │   ├── Coordinates/
│   │   │   ├── Square.cs
│   │   │   ├── File.cs
│   │   │   ├── Rank.cs
│   │   │   └── Direction.cs
│   │   ├── Pieces/
│   │   │   ├── Color.cs
│   │   │   ├── PieceType.cs
│   │   │   └── Piece.cs
│   │   ├── Moves/
│   │   │   ├── Move.cs
│   │   │   ├── MoveType.cs
│   │   │   └── MoveList.cs
│   │   ├── Bitboards/
│   │   │   ├── Bitboard.cs
│   │   │   ├── PrecomputedAttacks.cs
│   │   │   └── MagicBitboards.cs
│   │   ├── Zobrist/
│   │   │   └── Zobrist.cs
│   │   ├── Board/
│   │   │   ├── CastlingRights.cs
│   │   │   ├── BoardState.cs
│   │   │   ├── Board.cs
│   │   │   └── Fen.cs
│   │   ├── MoveGen/
│   │   │   └── MoveGenerator.cs
│   │   └── Evaluation/
│   │       ├── Value.cs
│   │       └── IEvaluator.cs
│   ├── Anak.Search/
│   │   ├── Anak.Search.csproj
│   │   ├── Arena/
│   │   │   ├── MCTSNode.cs
│   │   │   └── NodeArena.cs
│   │   ├── Transposition/
│   │   │   ├── TTEntry.cs
│   │   │   └── TranspositionTable.cs
│   │   ├── Time/
│   │   │   ├── SearchLimits.cs
│   │   │   └── TimeManager.cs
│   │   └── Mcts/
│   │       ├── MCTSParameters.cs
│   │       ├── SearchStatistics.cs
│   │       └── MCTSEngine.cs
│   ├── Anak.Data/
│   │   ├── Anak.Data.csproj
│   │   ├── PolyGlot/
│   │   │   ├── PolyGlotEntry.cs
│   │   │   ├── PolyGlotZobrist.cs
│   │   │   └── PolyGlotBook.cs
│   │   ├── Syzygy/
│   │   │   ├── SyzygyWdl.cs
│   │   │   ├── SyzygyDtz.cs
│   │   │   └── SyzygyTablebase.cs
│   │   └── Evaluation/
│   │       └── DefaultEvaluator.cs
│   └── Anak.Protocol/
│       ├── Anak.Protocol.csproj
│       ├── Uci/
│       │   ├── UciOption.cs
│       │   ├── UciHandler.cs
│       │   └── UciEngineAdapter.cs
│       ├── Bench/
│       │   └── BenchRunner.cs
│       └── Program.cs
└── tests/
    └── Anak.Tests/
        ├── Anak.Tests.csproj
        ├── Perft/
        │   ├── PerftRunner.cs
        │   └── PerftTests.cs
        ├── Core/
        │   ├── BitboardTests.cs
        │   ├── MagicTests.cs
        │   ├── MoveGenTests.cs
        │   ├── FenTests.cs
        │   └── RepetitionRuleTests.cs
        ├── Search/
        │   ├── MCTSTests.cs
        │   └── TranspositionTableTests.cs
        └── Data/
            ├── PolyGlotTests.cs
            └── EvaluatorTests.cs
```
