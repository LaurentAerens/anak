# Anak

[![CI](https://github.com/laerens/anak/actions/workflows/ci.yml/badge.svg)](https://github.com/laerens/anak/actions/workflows/ci.yml)
[![Release](https://github.com/laerens/anak/actions/workflows/release.yml/badge.svg)](https://github.com/laerens/anak/actions/workflows/release.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-11.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-preview%20(14%2F15)-239120.svg)](https://learn.microsoft.com/dotnet/csharp/)

**Anak** is an ultra-high-performance chess engine architected for the `.NET 11` runtime (`net11.0`), leveraging modern hardware intrinsics, zero-GC allocation discipline, Magic Bitboards, Monte Carlo Tree Search (MCTS) with PUCT and Transposition Table DAG subtree reuse, Syzygy endgame tablebase probing, PolyGlot opening book reader, and strict UCI/SPRT tournament compliance.

---

## Architecture Overview

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
```

### Key Highlights
- **.NET 11 & C# 14/15 Modern Runtime**: Built on cutting-edge .NET features, utilizing `System.Runtime.Intrinsics.X86.Bmi2`, `Popcnt`, and `BitOperations` for accelerated bitboard processing.
- **Zero-GC Allocation Discipline**: Move generation and search loops are designed to allocate zero heap memory during live analysis and search, relying on `InlineArray`, compact 16-bit structs, and pre-allocated node arenas.
- **Magic Bitboards & PEXT**: Sliding piece attacks (Rook, Bishop, Queen) are generated via PEXT instructions on supported hardware, falling back to Plain/Fancy Magic Bitboard hash tables.
- **Monte Carlo Tree Search (MCTS)**: Employs PUCT (Predictor Upper Confidence bounds applied to Trees), progressive widening, transposition table DAG reuse, and adaptive time management.
- **Endgame Tablebases (Syzygy)**: Native probing of Syzygy WDL (Win-Draw-Loss) and DTZ (Distance-to-Zero) tables for flawless endgame play.
- **PolyGlot Opening Books**: Direct binary reading of `.bin` books with Zobrist position hashing.

---

## Building & Running

### Prerequisites
- [.NET 11 SDK](https://dotnet.microsoft.com/download) (RC2 or higher)

### Build Solution
```bash
# Build the entire solution in Release mode
dotnet build Anak.sln -c Release
```

### Run Tests
```bash
# Execute unit and perft verification tests
dotnet test -c Release --verbosity normal
```

### Benchmark Mode
Run the built-in benchmark suite to measure search nodes-per-second (NPS) and move generation speed across representative positions:
```bash
dotnet run --project src/Anak.Protocol -c Release -- bench
```

### Perft Validation
Run Perft performance tests directly from the command line:
```bash
# Perft depth 5 from the starting position
dotnet run --project src/Anak.Protocol -c Release -- --perft 5

# Perft on custom FEN with Kiwipete
dotnet run --project src/Anak.Protocol -c Release -- --perft 4 --fen "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1"
```

---

## UCI Protocol Commands & Options

Anak communicates using the standard Universal Chess Interface (UCI) protocol and can be integrated into any standard chess GUI (e.g., Arena, Cutechess, Banksia, ChessBase).

### Core UCI Commands
| Command | Description |
|---|---|
| `uci` | Initializes the UCI engine interface and outputs supported options. |
| `isready` | Synchronizes with GUI and reports `readyok`. |
| `ucinewgame` | Resets internal search state, node arenas, and clears the transposition table. |
| `position startpos [moves ...]` | Sets the board to the standard starting position and plays moves. |
| `position fen <fen> [moves ...]` | Sets the board to the specified FEN string and plays moves. |
| `go [wtime <ms>] [btime <ms>] [winc <ms>] [binc <ms>]` | Initiates search under timed tournament conditions. |
| `go depth <n>` | Searches to a maximum depth of `<n>` plies. |
| `go nodes <n>` | Searches until `<n>` total nodes/simulations have been evaluated. |
| `go movetime <ms>` | Searches for a fixed duration of `<ms>` milliseconds. |
| `go infinite` | Searches indefinitely until a `stop` command is received. |
| `stop` | Halts active search immediately and returns the best move (`bestmove`). |
| `quit` | Exits the engine process. |

### Diagnostic Commands
| Command | Description |
|---|---|
| `perft <depth>` | Runs perft divide testing up to `<depth>` on the current position. |
| `bench` | Runs standardized benchmark suite across test positions and logs metrics. |

### Configurable UCI Options
| Option | Type | Default | Description |
|---|---|---|---|
| `Hash` | spin | `64` | Transposition Table size in megabytes (1 to 65536 MB). |
| `Threads` | spin | `1` | Number of worker threads for parallel search. |
| `SyzygyPath` | string | `""` | Directory path containing Syzygy `.rtbw` and `.rtbz` files. |
| `BookPath` | string | `""` | Path to PolyGlot `.bin` opening book file. |
| `MCTS_Cpuct` | spin | `140` | Exploration constant $C_{puct}$ scaled by 100 (1.40 default). |
| `MCTS_Temperature` | spin | `100` | Softmax temperature for move selection scaled by 100 (1.0 default). |

---

## SPRT Tournament Coordinator Integration

Anak is validated using Sequential Probability Ratio Testing (SPRT) under modern tournament coordinators.

### Fast-Chess Configuration
```bash
fast-chess \
  -engine cmd=./anak-linux-x64 name="Anak-Release" \
  -engine cmd=stockfish name="Stockfish" \
  -each proto=uci tc=10+0.1 \
  -sprt elo0=0 elo1=10 alpha=0.05 beta=0.05 \
  -rounds 2000 \
  -openings file=openings.pgn format=pgn order=random \
  -concurrency 8 \
  -draw number=40 count=8 score=10 \
  -resign movecount=3 score=600
```

### Cutechess-CLI Configuration
```bash
cutechess-cli \
  -engine name="Anak" cmd="./anak-linux-x64" proto=uci \
  -engine name="Opponent" cmd="./opponent" proto=uci \
  -each tc=40/60+0.6 timemargin=50 proto=uci \
  -sprt elo0=0 elo1=5 alpha=0.05 beta=0.05 \
  -rounds 1000 \
  -games 2 \
  -repeat \
  -openings file=openings.epd format=epd \
  -concurrency 4
```

---

## Perft Benchmarks

The move generator is strictly validated against canonical Perft leaf nodes:

### Standard Starting Position
`rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1`

| Depth | Leaf Nodes | Captures | Castles | Promotions | Checks | Checkmates |
|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| 1 | 20 | 0 | 0 | 0 | 0 | 0 |
| 2 | 400 | 0 | 0 | 0 | 0 | 0 |
| 3 | 8,902 | 34 | 0 | 0 | 12 | 0 |
| 4 | 197,281 | 1,576 | 0 | 0 | 469 | 8 |
| 5 | 4,865,609 | 82,719 | 0 | 0 | 27,351 | 347 |
| 6 | 119,060,324 | 2,812,008 | 0 | 0 | 809,099 | 10,828 |

### Position 2 (Kiwipete)
`r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1`

| Depth | Leaf Nodes | Captures | Castles | Promotions | Checks | Checkmates |
|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| 1 | 48 | 8 | 2 | 0 | 0 | 0 |
| 2 | 2,039 | 351 | 91 | 0 | 3 | 1 |
| 3 | 97,862 | 17,102 | 3,162 | 0 | 993 | 43 |
| 4 | 4,085,603 | 757,163 | 128,013 | 15,172 | 25,523 | 411 |
| 5 | 193,690,690 | 35,043,410 | 4,993,637 | 0 | 3,309,887 | 21,819 |

### Position 3
`8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1`

| Depth | Leaf Nodes | Captures | Checks | Checkmates |
|:---:|:---:|:---:|:---:|:---:|
| 1 | 14 | 1 | 2 | 0 |
| 2 | 191 | 14 | 10 | 0 |
| 3 | 2,812 | 209 | 267 | 0 |
| 4 | 43,238 | 3,348 | 1,680 | 17 |
| 5 | 674,624 | 52,051 | 52,957 | 0 |
| 6 | 11,030,083 | 940,350 | 452,473 | 2,733 |

### Position 4 (Mirrored Kiwipete)
`r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1`

| Depth | Leaf Nodes | Captures | Castles | Promotions | Checks |
|:---:|:---:|:---:|:---:|:---:|:---:|
| 1 | 6 | 0 | 0 | 0 | 0 |
| 2 | 264 | 87 | 6 | 48 | 10 |
| 3 | 9,467 | 1,021 | 0 | 120 | 38 |
| 4 | 422,333 | 131,393 | 7,795 | 60,032 | 15,492 |
| 5 | 15,833,292 | 2,046,173 | 0 | 329,461 | 200,568 |

### Position 5
`rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8`

| Depth | Leaf Nodes | Captures | Castles | Promotions | Checks |
|:---:|:---:|:---:|:---:|:---:|:---:|
| 1 | 44 | 0 | 0 | 0 | 2 |
| 2 | 1,486 | 398 | 0 | 0 | 83 |
| 3 | 62,379 | 9,842 | 1,438 | 6,364 | 2,336 |
| 4 | 2,103,487 | 344,499 | 0 | 0 | 114,845 |
| 5 | 89,941,194 | 14,797,820 | 1,760,207 | 7,858,660 | 3,116,423 |

---

## Developer Tools & Visualizer

For local engine debugging, interactive analysis, and development, an integrated visualizer tool is available in `tools/Anak.UI`:

```bash
dotnet run --project tools/Anak.UI -c Release
```

Features:
- Interactive click-to-move chessboard with legal move highlighting, check indicators, and board flipping.
- Real-time MCTS telemetry (depth, seldepth, node count, NPS, score, and principal variation line).
- Dynamic win-probability evaluation bar.
- FEN position import/export for rapid tactical debugging.
- Built-in engine self-play mode.

*Note: The UI tool is strictly for local developer use and is not packaged into the standalone engine distribution (`anak.exe`). See [tools/Anak.UI/README.md](tools/Anak.UI/README.md) for details and open-source attribution.*

---

## License

This project is licensed under the terms of the **GNU General Public License v3.0** (GPLv3). See the [LICENSE](LICENSE) file for complete details.

