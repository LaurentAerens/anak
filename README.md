# Anak

[![CI](https://github.com/laerens/anak/actions/workflows/ci.yml/badge.svg)](https://github.com/laerens/anak/actions/workflows/ci.yml)
[![Release](https://github.com/laerens/anak/actions/workflows/release.yml/badge.svg)](https://github.com/laerens/anak/actions/workflows/release.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-11.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-preview%20(14%2F15)-239120.svg)](https://learn.microsoft.com/dotnet/csharp/)

**Anak** is an ultra-high-performance chess engine authored by **Laurent Aerens** (Belgium), architected for the `.NET 11` runtime (`net11.0`), leveraging modern hardware intrinsics, zero-GC allocation discipline, Magic Bitboards, a tournament-grade **Alpha-Beta (Negamax / PVS)** search engine with Lazy SMP alongside an alternative **Monte Carlo Tree Search (MCTS)** engine, pure managed **Syzygy endgame tablebase probing**, a **PolyGlot opening book reader**, a **Multi-Tier NNUE evaluation pipeline**, and complete UCI/SPRT tournament compliance.

---

## Architecture Overview

```mermaid
flowchart TD
    subgraph Protocol["Anak.Protocol"]
        UCI["UCI Protocol Loop (Async Stdin/Stdout)"]
        Diagnostics["Bench & Perft Diagnostic CLI"]
        Analyze["Position & PGN Game Analysis CLI"]
    end

    subgraph Search["Anak.Search (ISearchEngine)"]
        direction TB
        subgraph Minimax["AlphaBetaEngine (Primary Minimax)"]
            PVS["Principal Variation Search (Fail-Soft Negamax)"]
            ID["Iterative Deepening & Aspiration Windows"]
            MovePicker["MovePicker (TT Move, SEE, Killers, History)"]
            Pruning["Selectivity (RFP, NMP, Singular/Check Ext, LMR)"]
            QS["Quiescence Search (Leaf Stand-Pat Verification)"]
            LazySMP["Lock-Free Lazy SMP Worker Threads"]
        end

        subgraph MCTS["MCTSEngine (Alternative Search)"]
            NodeArena["Zero-GC Pre-allocated Node Arena"]
            PUCT["PUCT Selection & Expansion"]
            DAG["Transposition Table Subtree DAG Reuse"]
        end

        TT["64-Byte Cacheline Transposition Table"]
        TimeMgr["Dynamic Time Management"]
    end

    subgraph Data["Anak.Data (Evaluation & Books)"]
        direction TB
        Book["PolyGlot Opening Book (.bin Reader)"]
        Syzygy["Pure Managed Syzygy EGTB (100% C# WDL & DTZ)"]

        subgraph NNUE["MultiTierNnueEvaluator Pipeline"]
            direction TB
            Classifier["PawnTopology & Board Classifier"]

            subgraph FourModels["The 4 Core NNUE Networks"]
                M_Open["1. Open-NNUE (open.nnue): 768 to 512x2 to 64 to 1 - Open Topologies"]
                M_Closed["2. Closed-NNUE (closed.nnue): 768 to 512x2 to 64 to 1 - Locked Chains & Maneuvering"]
                M_Micro["3. Micro-NNUE (micro.nnue): 768 to 16x2 to 1 - Tactical Blunder Watchdog"]
                M_Vol["4. Volatility-NNUE (volatility.nnue): 768 to 16x2 to 1 - Sharpness & Tension Estimator"]
            end

            Accumulator["Vector256 / AVX2 Incremental Accumulator Stack"]
            Inference["Vectorized INT8/INT16 SIMD Inference Engine"]
        end
    end

    subgraph Core["Anak.Core"]
        Bitboards["64-bit Bitboards & LERF Coordinates"]
        Magics["Plain/Fancy Magic Bitboards + PEXT Intrinsics"]
        MoveGen["Fast Pseudo-Legal & Legal MoveGen (Zero Heap Alloc)"]
        SEE["Static Exchange Evaluation (SEE)"]
        Zobrist["64-bit Zobrist Hash Keys & Rolling History"]
        Topology["Pawn Topology & Dynamic Openness Metric"]
    end

    Protocol --> Search
    Search --> Data
    Search --> Core
    Data --> Core
    Minimax --> TT
    MCTS --> TT
    Minimax --> TimeMgr
    MCTS --> TimeMgr
    Classifier --> FourModels
    FourModels --> Accumulator --> Inference
```

### The 4 Core NNUE Neural Networks

Anak deploys a multi-tier neural network architecture composed of **4 specialized quantized networks**, routing evaluations dynamically based on pawn structure, phase, and tactical tension:

1. **Open-NNUE (`open.nnue`)**
   - **Architecture**: Standard NNUE ($768 \to 512\times 2 \to 64 \to 1$).
   - **Quantization**: INT8 feature transformer weights, INT16 biases, ClippedReLU ($[0, 64]$) activation.
   - **Domain / Topology**: Positions with open pawn topologies ($\ge 0.50$ continuous openness score), characterized by open/semi-open files, active bishop diagonals, center pawn liquidations, and fluid tactical dynamics.
   - **Role in Search**: Evaluates candidate leaf nodes when root or leaf topology is open. Includes smooth boundary interpolation ($[0.42, 0.58]$ openness score) between Open and Closed experts to prevent evaluation jitter across transpositions.

2. **Closed-NNUE (`closed.nnue`)**
   - **Architecture**: Standard NNUE ($768 \to 512\times 2 \to 64 \to 1$).
   - **Quantization**: INT8 feature transformer weights, INT16 biases, ClippedReLU ($[0, 64]$) activation.
   - **Domain / Topology**: Positions with locked pawn structures ($< 0.50$ continuous openness score), head-to-head locked pawn chains (rams), blocked centers, and strategic piece maneuvering.
   - **Role in Search**: Primary positional expert for closed games, closed defenses (e.g. French Defense, King's Indian, locked Caro-Kann lines), and maneuvering endgames.

3. **Micro-NNUE (`micro.nnue`)**
   - **Architecture**: Ultra-compact 1-SIMD register network ($768 \to 16\times 2 \to 1$).
   - **Speed & Footprint**: Evaluates at ~20–30M NPS with an ultra-lightweight memory footprint (~12 KB binary file). Exactly fits into a single 256-bit AVX2 register per perspective.
   - **Domain / Training**: Trained on tactical swing positions, blunders ($\ge 1.5$ pawn swing), and critical counter-tactics.
   - **Role in Search**: Operates as a zero-overhead tactical watchdog. In `AlphaBetaEngine`, it provides fast stand-pat pre-screening in Quiescence Search and early leaf candidate pruning; in `MCTSEngine`, it runs tactical watchdog burst evaluations on deep candidate leaves.

4. **Volatility-NNUE (`volatility.nnue`)**
   - **Architecture**: Compact micro network ($768 \to 16\times 2 \to 1$).
   - **Output**: Tactical sharpness / position instability metric normalized in $[0, 1000]$.
   - **Domain / Features**: Trained to predict position volatility, mutual pawn tension, queen presence, king flank exposure, and opposite-side castling sharpness.
   - **Role in Search**: Guides dynamic search selectivity, adjusts aspiration window margins, controls MCTS visit allocation and softmax temperature, and triggers singular extensions during high-volatility tactical phases (with zero-allocation bitboard tension fallback if absent).

> [!NOTE]
> **Extended Expert Pool**: In addition to the 4 core models above, Anak's topology routing engine can optionally load 4 supplementary full experts: `endgame.nnue` ($\le 14$ pieces), `queenless.nnue` (both queens off board), `imbalance.nnue` (material imbalance $\ge 2$ pawns or piece mismatch), and `attack.nnue` (opposite-side castling king hunts), classified via `PawnTopology.ClassifyExpert`.

### Key Highlights
- **.NET 11 & C# 14/15 Modern Runtime**: Built on cutting-edge .NET features, utilizing `System.Runtime.Intrinsics.X86.Bmi2`, `Popcnt`, AVX2, and `BitOperations` for accelerated bitboard processing and SIMD tensor evaluation.
- **Dual Search Architectures**:
  - **Alpha-Beta (Minimax/PVS)** (*Default*): Features fail-soft Negamax with Iterative Deepening, Aspiration Windows, Reverse Futility Pruning (RFP), Null Move Pruning (NMP), Singular/Check Extensions, Topology-adaptive Late Move Reductions (LMR), staged MovePicker with Static Exchange Evaluation (SEE), comprehensive history heuristics, and lock-free Lazy SMP.
  - **Monte Carlo Tree Search (MCTS)** (*Alternative*): Features PUCT exploration, progressive widening, transposition DAG subtree reuse, and adaptive visit distribution.
- **Zero-GC Allocation Discipline**: Move generation and search loops are designed to allocate zero heap memory during live analysis and search, relying on `InlineArray`, compact 16-bit structs, and pre-allocated node arenas.
- **Multi-Tier NNUE Architecture**: Root-pinned Open-NNUE and Closed-NNUE experts evaluated via AVX2 SIMD dot products, paired with an ultra-compact Small/Micro-NNUE (1-SIMD register, ~20–30M NPS) tactical blunder watchdog and fast search pre-screen.
- **Pure Managed Syzygy Endgame Tablebases**: Native probing of Syzygy WDL (Win-Draw-Loss) and DTZ (Distance-to-Zero) tables written in 100% pure managed C# without external C/C++ shared libraries. Bundles complete 3-piece and 4-piece Syzygy tables (70 files, 4.14 MB) created by **Ronald de Man** with auto-discovery and full UCI support.
- **PolyGlot Opening Books**: Direct binary reading of `.bin` books with weighted random roulette-wheel move selection. Ships with the tournament-proven `Performance.bin` opening book (92,954 master moves) compiled by **Marc Lacrosse**.
- **SPSA Parameter Tuning**: Built-in Python harness for Simultaneous Perturbation Stochastic Approximation (SPSA) to calibrate pruning margins and search parameters.
- **Automated Elo Benchmarks**: Fast-chess automated tournament runner with local baseline archiving and SPRT progression tracking.

---

## Building & Running

### Prerequisites
- [.NET 11 SDK](https://dotnet.microsoft.com/download) (RC2 or higher)
- Optional for NNUE training & tuning: Python 3.11+ with PyTorch

### Build Solution
```bash
# Build the entire solution in Release mode
dotnet build Anak.sln -c Release
```

### Run Tests
```bash
# Execute C# unit, perft, and search regression tests
dotnet test -c Release --verbosity normal

# Execute Python NNUE training pipeline tests
pytest
```

### Benchmark Modes

#### 1. Engine UCI Bench
Run the standardized UCI benchmark across representative middlegame and endgame positions:
```bash
# Default bench (depth 10, 1 thread, 64 MB hash)
dotnet run --project src/Anak.Protocol -c Release -- bench

# Custom bench parameters: bench [depth] [threads] [hash_mb]
dotnet run --project src/Anak.Protocol -c Release -- bench 12 4 128
```

#### 2. Micro-Benchmarks (BenchmarkDotNet)
Run hardware-level micro-benchmarks for move generation, bitboard operations, and NNUE vectorization:
```bash
dotnet run --project tests/Anak.Benchmarks -c Release
```

### Position & PGN Analysis CLI
Analyze individual positions or full PGN games directly from the command line:
```bash
# Analyze a custom FEN position to depth 14 with 4 threads and 128 MB hash
dotnet run --project src/Anak.Protocol -c Release -- analyze --fen "r1bqkb1r/pppp1ppp/2n5/4p3/2B1n3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 5" --depth 14 --threads 4 --hash 128

# Analyze a full PGN game file
dotnet run --project src/Anak.Protocol -c Release -- analyze --pgn game.pgn --depth 12
```

### Perft Validation
Run Perft performance tests directly from the command line:
```bash
# Perft depth 5 from the starting position
dotnet run --project src/Anak.Protocol -c Release -- --perft 5

# Perft on custom FEN (Kiwipete)
dotnet run --project src/Anak.Protocol -c Release -- --perft 4 --fen "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1"
```

---

## UCI Protocol Commands & Options

Anak communicates using the standard Universal Chess Interface (UCI) protocol and can be integrated into any chess GUI (e.g., Arena, Cutechess, Banksia, ChessBase).

### Core UCI Commands
| Command | Description |
|---|---|
| `uci` | Initializes the UCI engine interface and outputs supported options. |
| `isready` | Synchronizes with the GUI and reports `readyok`. |
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
| `SearchAlgorithm` | combo | `Minimax` | Search algorithm: `Minimax` (Alpha-Beta / PVS) or `Mcts` (Monte Carlo Tree Search). |
| `Hash` | spin | `64` | Transposition Table size in megabytes (1 to 65,536 MB). |
| `Threads` | spin | `1` | Number of concurrent worker threads (1 to 256). |
| `UseNnue` | check | `true` | Enables Multi-Tier NNUE neural network evaluation. |
| `NnuePath` | string | `"./networks"` | Directory containing `open.nnue`, `closed.nnue`, `micro.nnue`, and `volatility.nnue`. |
| `BlunderWatchdog` | check | `true` | Enables Small/Micro-NNUE tactical watchdog bursts on candidate leaves. |
| `OwnBook` | check | `true` | Enables PolyGlot opening book probing. |
| `BookFile` | string | `"book.bin"` | Path to PolyGlot `.bin` opening book file. |
| `BookMode` | combo | `WeightedRandom` | Opening book move picker: `WeightedRandom`, `BestMove`, `UniformRandom`. |
| `SyzygyPath` | string | `""` | Directory containing Syzygy `.rtbw` and `.rtbz` tablebase files. |
| `SyzygyProbeDepth`| spin | `1` | Minimum search depth to probe Syzygy tablebases (1 to 100). |
| `MoveRandomness` | spin | `10` | MCTS move randomness factor (0 to 100). |
| `Temperature` | spin | `0` | MCTS Softmax temperature for move selection (0 to 100). |
| `LmrBase` | spin | `76` | Base Late Move Reduction constant ($0.76$). |
| `LmrDivision` | spin | `227` | Logarithmic divisor scaling LMR reductions ($2.27$). |
| `RfpMargin` | spin | `73` | Reverse Futility Pruning (Static Null Move) margin per depth ($73$ cp). |
| `NmpBase` | spin | `3` | Dynamic Null Move Pruning base reduction ($R = 3$). |
| `AspirationDelta` | spin | `26` | Search aspiration window radius ($\pm 26$ cp). |
| `ConthistReward` | spin | `2500` | 1-ply Continuation history reward threshold ($r{-}{-}$). |
| `ConthistPenalty` | spin | `4000` | 1-ply Continuation history penalty threshold ($r{+}{+}$). |
| `HistoryThreshold` | spin | `2000` | Butterfly history score threshold for LMR ($\pm 2000$). |
| `ScoreDropThreshold` | spin | `50` | Score drop panic time extension threshold ($50$ cp). |

### Shipped Opening Book & Endgame Tablebases

Anak includes tournament-ready opening books and endgame tablebases directly in the repository and release archives:

1. **PolyGlot Master Opening Book (`book.bin`)**
   - **Origin & Credit**: Compiled and curated by **Marc Lacrosse** (based on `Performance.bin`).
   - **Scale**: Contains **92,954** canonical opening positions covering all classical and modern setups (1.e4, 1.d4, 1.c4, 1.Nf3, Sicilian, French, Caro-Kann, Indian Defenses).
   - **Selection Modes**: Controlled via `BookMode` (`WeightedRandom`, `BestMove`, `UniformRandom`).
   - **Auto-Discovery**: Located automatically in the working directory, binary root, or repository hierarchy without manual setup.

2. **Syzygy Endgame Tablebases (`syzygy/`)**
   - **Origin & Credit**: Designed and generated by **Ronald de Man** ([syzygy1](https://github.com/syzygy1)). Hosted on Sesse's tablebase mirror ([http://tablebase.sesse.net/syzygy/](http://tablebase.sesse.net/syzygy/)).
   - **Scope Shipped**: Complete **3-piece** and **4-piece** endgame suite (35 `.rtbw` WDL files + 35 `.rtbz` DTZ files = **70 files**, 4.14 MB total). Covers all lone king and piece/pawn endings (KQvK, KRvK, KBBvK, KBNvK, KQvKR, KRvKB, KPvKP, etc.).
   - **Auto-Discovery**: Automatically probed from the local `./syzygy` directory when `SyzygyPath` is left default (`""`).
   - **Larger Tables (5 & 6 Pieces)**: To use 5-piece (~940 MB) or 6-piece (~150 GB) Syzygy tables, download them from the mirrors and configure `setoption name SyzygyPath value <path>`.

For full license and attribution details, see [CREDITS.md](CREDITS.md).

---

## Multi-Tier NNUE Evaluation & Training Pipeline

Anak features a multi-expert neural architecture paired with a tactical watchdog and volatility estimator to eliminate evaluation jitter and guide search selectivity:

1. **Open-NNUE** (`open.nnue`): Standard expert ($768 \to 512\times 2 \to 64 \to 1$) trained on open positions ($\ge 0.50$ pawn openness score).
2. **Closed-NNUE** (`closed.nnue`): Standard expert ($768 \to 512\times 2 \to 64 \to 1$) trained on locked pawn chains and closed maneuvering ($< 0.50$ openness score).
3. **Small / Micro-NNUE** (`micro.nnue`): Ultra-fast 1-SIMD register model ($768 \to 16\times 2 \to 1$, ~20–30M NPS) deployed as a deep tree tactical watchdog and fast pre-screen.
4. **Volatility-NNUE** (`volatility.nnue`): Ultra-fast micro model ($768 \to 16\times 2 \to 1$) predicting dynamic position tension and tactical instability in $[0, 1000]$.

### High-Speed Pure-Python Pipeline (>1,000,000 pos/sec)
The training pipeline located in `training/` features:
- **Zero-Copy Memory-Mapped Training**: Contiguous 144-byte `.binpack` records mapped directly via `numpy.memmap`.
- **GPU Sparse Scatter**: In-VRAM feature index scattering to 768-dim input tensors.
- **Multi-Source Data Hub**: Downloads and preprocesses data from Stockfish, TCEC, Leela Chess Zero, and Lichess.
- **Hardware Auto-Tuning**: Hardware scanner (`hardware.py`) maximizing CPU workers, GPU streams, TF32, and pinned memory DMA.
- **Automated Deployment**: Quantization and export script (`export.py`, `deploy_best.py`) targeting the engine's `./networks` folder.

```bash
# 1. Generate instant baseline networks for development:
python training/generate_synthetic.py

# 2. Or run end-to-end multi-tier training on tournament archives:
python training/train_all.py --epochs 30 --batch-size 8192
```

See the [**Training Documentation & Pipeline Guide**](training/README.md) for full details.

---

## SPSA Search Heuristics Tuning

An automated **Simultaneous Perturbation Stochastic Approximation (SPSA)** tuning harness is available in `tools/tuning/` to optimize search heuristics and pruning margins:

- **Targeted Heuristics**: `LmrBase`, `LmrDivision`, `RfpMargin`, `NmpBase`, `AspirationDelta`, `ConthistReward`, `ConthistPenalty`, `HistoryThreshold`, and `ScoreDropThreshold`.
- **Features**: Simultaneous gradient approximation, JSON progress tracking, automatic C# snippet generation, and multi-core parallel games.

```bash
# Verify SPSA gradient steps with dry-run mode:
python tools/tuning/spsa.py --dry-run --steps 10 --games-per-step 20

# Run multi-threaded tuning with fastchess:
python tools/tuning/spsa.py --steps 50 --games-per-step 30 --concurrency 8 --tc 2+0.02
```

See [tools/tuning/README.md](tools/tuning/README.md) for details.

---

## Elo Rating Benchmark & SPRT Tournaments

An automated tournament benchmark tool using [`fast-chess`](https://github.com/Disservin/fastchess) is pre-configured in `tools/fastchess/` to evaluate Anak's Elo progression against archived baseline releases or calibrated reference levels of **Stockfish 19**:

```cmd
# Run self-play benchmark against latest baseline (1000 games at 3+0.05)
.\run_elo_bench.bat

# Standard match with SPRT early stopping (elo0=0, elo1=30)
.\run_elo_bench.bat --sprt

# Quick smoke test against baseline (50 games at 3+0.05)
.\run_elo_bench.bat --quick

# Re-calibrate against Stockfish 1800 Elo
.\run_elo_bench.bat --opponent stockfish --elo 1800 --games 1000
```

The runner automatically compiles `anak.exe`, matches candidate vs baseline using balanced opening suites with color swapping, computes 95% confidence intervals, and records results to [BENCHMARKS.md](BENCHMARKS.md) and [`tools/fastchess/ratings.json`](tools/fastchess/ratings.json).

### Rating History Summary
| Version | Search Engine | Estimated Elo | Reference Opponent |
| :--- | :--- | :---: | :--- |
| [**v0.0.1**](https://github.com/laerens/anak/releases/tag/v0.0.1) | MCTS + Multi-Tier NNUE | **1616** | Stockfish 19 (1800 Elo) |
| [**v0.0.2**](https://github.com/laerens/anak/releases/tag/v0.0.2) | MCTS + Multi-Tier NNUE | **1616** | Baseline v0.0.1 |
| [**v0.0.3**](https://github.com/laerens/anak/releases/tag/v0.0.3) | Alpha-Beta (Negamax/PVS) | **1659** | Stockfish 19 (1800 Elo) |
| [**v0.0.4**](https://github.com/laerens/anak/releases/tag/v0.0.4) | Alpha-Beta + NNUE Retrain + Book + Syzygy + SPSA | **1678** | Anak v0.0.3 (+18.8 Elo) |
| [**v1.0.0**](https://github.com/laerens/anak/releases/tag/v1.0.0) | Multi-Tier NNUE (6 Experts) + Alpha-Beta PVS + Syzygy + Unified TT | **2261** | Stockfish Ladder / current_dev (+31 Elo) |
| [**v1.0.1**](https://github.com/laerens/anak/releases/tag/v1.0.1) | 1D Flattened ContinuationHistory Table (+5.8% NPS) | **2264** | Anak v1.0.0 (+3.1 Elo) |
| [**v1.0.2**](https://github.com/laerens/anak/releases/tag/v1.0.2) | Inline Fixed Mailbox Piece[64] in Board | **2270** | Anak v1.0.1 (+6.3 Elo) |
| [**v1.0.3**](https://github.com/laerens/anak/releases/tag/v1.0.3) | Bypass Micro-NNUE Accumulator Updates in Search | **2274** | Anak v1.0.2 (+4.1 Elo) |
| [**v1.0.4**](https://github.com/laerens/anak/releases/tag/v1.0.4) | Capture History Malus on Failed Captures | **2282** | Anak v1.0.3 (+8.2 Elo) |
| [**v1.0.5**](https://github.com/laerens/anak/releases/tag/v1.0.5) | Correction History Bound Filtering & Depth Weighting (-46% nodes) | **2487** | Anak v1.0.4 (+204.5 Elo) |
| [**v1.0.6**](https://github.com/laerens/anak/releases/tag/v1.0.6) | Cached Capture SEE in MovePicker & Shallow SEE Pruning | **2503** | Anak v1.0.5 (+16.3 Elo) |
| [**v1.0.7**](https://github.com/laerens/anak/releases/tag/v1.0.7) | Correction History Side-to-Move Partitioning `[STM, PawnKey]` | **2504** | Anak v1.0.6 (+1.1 Elo) |

See [BENCHMARKS.md](BENCHMARKS.md) and [tools/fastchess/README.md](tools/fastchess/README.md) for complete details.

---

## SPRT Tournament Coordinator Integration

Anak is validated using Sequential Probability Ratio Testing (SPRT) under modern tournament coordinators.

### Fast-Chess Configuration
```bash
fast-chess \
  -engine cmd=./anak name="Anak-Release" \
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
  -engine name="Anak" cmd="./anak" proto=uci \
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
- Real-time search telemetry (depth, seldepth, node count, NPS, score, and principal variation line).
- Dynamic win-probability evaluation bar.
- FEN position import/export for rapid tactical debugging.
- Built-in engine self-play mode.

*Note: The UI tool is strictly for local developer use and is not packaged into the standalone engine distribution (`anak.exe`). See [tools/Anak.UI/README.md](tools/Anak.UI/README.md) for details and open-source attribution.*

---

## Benchmarking & Evaluation Guidelines

Anak follows a disciplined **3-Tier Testing Pyramid** (Sanity Gate &rarr; SPRT Validation &rarr; Moderate Time Control) to validate engine optimizations, balance compute budgets, and eliminate battery/throttling variance via deterministic fixed-node testing.

See the complete guide in **[`docs/BENCHMARKING_POLICY.md`](docs/BENCHMARKING_POLICY.md)**.

---

## Author & Credits

<img align="right" width="160" src="assets/logo.jpg" alt="Anak Chess Engine Logo" />

- **Author**: **Laurent Aerens** (Belgium)
- **GitHub**: [@laerens](https://github.com/laerens)

See [CREDITS.md](CREDITS.md) for detailed third-party acknowledgments and asset licenses.

---

## License

This project is licensed under the terms of the **GNU General Public License v3.0** (GPLv3). See the [LICENSE](LICENSE) file for complete details.
