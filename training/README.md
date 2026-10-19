# Anak NNUE Training Pipeline

High-performance, pure-Python PyTorch training pipeline for Anak's Multi-Tier Neural Evaluation Architecture, supporting multi-source data ingestion (Stockfish, Generated, TCEC, Leela, Lichess), metric queries ("boards with less pieces or no queen"), one-time preprocessing, and blazing-fast memory-mapped binary packs (`.binpack`).

---

## Architecture Overview

Anak utilizes three specialized quantized neural networks:
1. **Open-NNUE** (`StandardNNUE`, 768 $\to$ 256x2 $\to$ 32 $\to$ 1): Trained on positions with open pawn topologies ($\ge 0.50$ openness score, active diagonals, open files).
2. **Closed-NNUE** (`StandardNNUE`, 768 $\to$ 256x2 $\to$ 32 $\to$ 1): Trained on closed pawn structures (locked pawn chains, maneuvering, outpost control).
3. **Micro-NNUE / Minimal** (`MicroNNUE`, 768 $\to$ 16x2 $\to$ 1): Ultra-fast 1-SIMD register network trained as a tactical watchdog for blunder and trap verification.

---

## High-Speed Pure-Python Pipeline (>1,000,000 pos/sec)

Unlike slow streaming text PGN parsers that re-decode chess games on every epoch, Anak's training pipeline uses a **two-stage architecture**:
1. **One-Time Preprocessing (`preprocess.py`)**:
   - Parses multi-source raw files once across all CPU cores.
   - Computes pawn openness, piece counts, queen presence, material imbalance, and HalfKP feature indices.
   - Automatically pre-splits into `open.binpack`, `closed.binpack`, and `micro.binpack` (minimal model).
2. **Zero-Copy Memory-Mapped Training (`train.py` & `fast_loader.py`)**:
   - Employs 144-byte contiguous binary records with zero text parsing and zero Python GC overhead.
   - Maps datasets directly from disk with `numpy.memmap`.
   - Uses **GPU-accelerated sparse scatter** (`scatter_` directly in CUDA VRAM) to convert sparse piece indices to 768-dim input tensors in microseconds, achieving over **1,000,000 positions/sec** in 100% pure Python!

---

## Setup & Installation

```bash
cd training
python -m venv .venv
# On Windows:
.venv\Scripts\activate
# On Linux/macOS:
source .venv/bin/activate

pip install -r requirements.txt
```

---

## Quickstart: All-in-One Training Pipeline (`train_all.py`)

To run the entire pipeline end-to-end (sensible data gathering $\to$ one-time preprocessing $\to$ training Open, Closed & Micro models for 30 epochs $\to$ auto-exporting quantized `.nnue` files to `networks/`):

```bash
python train_all.py --epochs 30 --batch-size 8192
```

---

## 1. Multi-Source Training Data Hub (`download_data.py`)

Anak supports combining data from 5 major sources:
- **ST / Stockfish**: High-depth engine evaluations and self-play test matches.
- **Generated**: Query-guided self-play generated on your CPU cores with local Stockfish.
- **TCEC**: Grandmaster engine tournament seasons and Cup archives.
- **Leela (Lc0)**: Deep neural network self-play archives and Superfinal games.
- **Lichess**: Evaluated broadcast master games and tactical blunder puzzles.

```bash
# Download TCEC Season 25:
python download_data.py --source tcec --season 25 --division all

# Download curated Stockfish evaluations:
python download_data.py --source stockfish

# Download Leela Chess Zero (Lc0) tournament games:
python download_data.py --source leela

# Download Lichess master games with Stockfish evals:
python download_data.py --source lichess

# Download a curated combo across all sources:
python download_data.py --source all
```

---

## 2. Query Metric Engine & Steered Data Generation

You can generate custom data or filter existing datasets using an expressive query DSL and metric constraints:

### Available Query Metrics:
- **Piece counts**: `pieces` / `piece_count` (e.g. `pieces <= 16`, `pieces <= 10`)
- **Queens**: `has_queens`, `no_queens` / `queens == 0`, `queens_count`
- **Topology**: `openness` (0.0 to 1.0), `is_open` ($\ge 0.50$), `is_closed` ($< 0.50$)
- **Tactics & Blunders**: `is_blunder` ($\Delta \text{eval} \ge 1.5$ or severe swing), `abs_eval`, `material_imbalance`
- **Source**: `source == 'stockfish'`, `source == 'tcec'`, `source == 'leela'`, `source == 'generated'`

### Generating Query-Steered Data (`generate_data.py`):
```bash
# Generate 50,000 queenless positions (no queen):
python generate_data.py --no-queens --positions 50000

# Generate 50,000 endgame positions with 16 or fewer pieces:
python generate_data.py --max-pieces 16 --positions 50000

# Combined query: queenless endgames directly to binary pack:
python generate_data.py --query "pieces <= 16 and not has_queens" --positions 50000 --format binpack

# Tactical blunder data for the Micro/Minimal model:
python generate_data.py --mode blunders --positions 50000
```

---

## 3. One-Time Preprocessing & Pre-Splitting (`preprocess.py`)

Run this once over your raw data directory (`training/data/`). It parses all files in parallel and produces pre-split binary packs:

```bash
# Pre-split all raw data into open.binpack, closed.binpack, and micro.binpack:
python preprocess.py --input ./data --output ./data/preprocessed

# Preprocess with a custom query filter:
python preprocess.py --input ./data --query "pieces <= 16 and not has_queens"

# Preprocess using a multi-slice recipe specifying quotas ('how much of what'):
python preprocess.py --input ./data --recipe recipes/sample_recipe.json
```

### Dataset Recipe Specification (`how much of what`):
Example `recipes/sample_recipe.json`:
```json
{
  "open_middlegame": {
    "query": "openness >= 0.50 and pieces > 16 and has_queens",
    "ratio": 0.35
  },
  "queenless_endgame": {
    "query": "no_queens and pieces <= 16",
    "ratio": 0.25
  },
  "deep_endgame": {
    "query": "pieces <= 10",
    "ratio": 0.15
  },
  "tactical_blunders": {
    "query": "is_blunder or abs_eval >= 2.0",
    "ratio": 0.15
  },
  "closed_chains": {
    "query": "openness < 0.45",
    "ratio": 0.10
  }
}
```

---

## 4. Training the Models at Maximum Speed (`train.py`)

When `training/data/preprocessed/<model>.binpack` is present, `train.py` automatically enables the high-speed memory-mapped loader:

```bash
# 1. Train Open-NNUE on preprocessed binary pack:
python train.py --model open --epochs 40 --batch-size 8192

# 2. Train Closed-NNUE:
python train.py --model closed --epochs 40 --batch-size 8192

# 3. Train Micro-NNUE (Tactical Blunder Watchdog / Minimal model):
python train.py --model micro --epochs 30 --batch-size 8192 --lr 2e-3

# Or explicitly specify the binary pack:
python train.py --model open --binpack ./data/preprocessed/open.binpack
```

> [!TIP]
> With the memory-mapped binpack, an epoch of 1,000,000 positions completes in just a few seconds on a modern GPU, with zero CPU throttling.

---

## 5. Model Deployment to Anak

When training finishes, `train.py` **automatically** quantizes the best checkpoint and deploys it directly into `networks/` (`open.nnue`, `closed.nnue`, or `micro.nnue`).

To manually batch deploy all best checkpoints at any time:
```bash
python deploy_best.py
```
