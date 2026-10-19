# Anak NNUE Training Pipeline

PyTorch-based training pipeline for Anak's Multi-Tier Neural Evaluation Architecture, inspired by Stockfish's `nnue-pytorch`.

---

## Architecture Overview

Anak utilizes three specialized quantized neural networks:
1. **Open-NNUE** (`StandardNNUE`, 768 $\to$ 256x2 $\to$ 32 $\to$ 1): Trained on positions with open pawn topologies ($\ge 0.50$ openness score, active diagonals, open files).
2. **Closed-NNUE** (`StandardNNUE`, 768 $\to$ 256x2 $\to$ 32 $\to$ 1): Trained on closed pawn structures (locked pawn chains, maneuvering, outpost control).
3. **Micro-NNUE** (`MicroNNUE`, 768 $\to$ 16x2 $\to$ 1): Ultra-fast 1-SIMD register network trained as a tactical watchdog for blunder and trap verification.

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

## Dataset: Automated TCEC Downloader

Anak includes an automated downloader script ([`download_tcec.py`](file:///c:/Users/laerens/source/repos/Personal/anak/training/download_tcec.py)) that connects to `tcec-chess.com`, downloads archives, and extracts the PGN files with full engine evaluations:

### 1. Download via Web URL:
```bash
python download_tcec.py --url "https://tcec-chess.com/#div=all&game=1&season=25" --output ./data
```

### 2. Download via Season and Division Arguments:
```bash
# Download complete Season 25 (all divisions, ~42 MB zip / 140 MB PGN)
python download_tcec.py --season 25 --division all --output ./data

# Download only Season 25 Superfinal
python download_tcec.py --season 25 --division sf --output ./data
```

### Handling TCEC High Draw Rate (~75%)
TCEC games frequently end in draws between elite engines. The dataset pipeline mitigates draw-bias by:
- Parsing the move-by-move engine evaluation comments (`{%eval ...}`).
- Using `TcecBlendedLoss`, which balances true game outcomes ($0.5$ for draws) with deep search evaluations:
  $$\mathcal{L} = \lambda \cdot \mathcal{L}_{\text{WDL}} + (1 - \lambda) \cdot \mathcal{L}_{\text{Huber}}$$

---

## Training the Models

The training script automatically discovers all `.pgn` and `.pgn.gz` files inside `./data/` or `training/data/`. You can omit `--data` or explicitly point it to a file or directory.

### 1. Train the Open-NNUE Model
```bash
# Auto-discovers games in ./data
python train.py --model open --epochs 40 --batch-size 4096 --output ./checkpoints

# Or explicitly pass the data folder / PGN file:
python train.py --model open --data ./data --epochs 40 --batch-size 4096
```

### 2. Train the Closed-NNUE Model
```bash
python train.py --model closed --epochs 40 --batch-size 4096 --output ./checkpoints
```

### 3. Train the Small / Micro-NNUE Model (Deep Tree Tactical & Blunder Watchdog)

The **Small NNUE** (also called **Micro-NNUE**) is an ultra-fast, compact model ($768 \to 16\times 2 \to 1$) engineered for high-throughput tactical evaluation (~20–30M NPS, fitting within a single 256-bit SIMD register).

#### How the Small NNUE Operates in Anak
1. **Zero-Jitter Tactical Verification**: Rather than swapping the full expert model at deep plies (which causes evaluation jitter and search instability), Anak keeps the root-pinned expert (Open or Closed) for primary node valuation and deploys the Small NNUE as an **asymmetric tactical watchdog**.
2. **Watchdog Bursts on Deep Candidates**: When search depth grows large on promising candidate lines ($Q \ge 0.45$ and $\text{depth} \ge 6$), Anak executes a fast 1–2 ply tactical burst with the Small NNUE to detect hidden refutations, missed traps, and zugzwang.
3. **Automated Tactical Data Filtering**: When training with `--model small` (or `--model micro`), the dataset reader automatically extracts:
   - **Tactical Swing Positions**: Plies with severe evaluation swings ($\Delta \text{eval} \ge 1.5$ pawns).
   - **Simplified & Deep Endgames**: Positions with $\le 16$ pieces remaining on the board.
4. **Asymmetric Blunder Loss (`BlunderLoss`)**: Uses an asymmetric Huber loss function with a **5x gradient penalty** on false negatives (positions where a blunder or trap occurred, but the model erroneously predicted the position was safe).

#### Training Command:
You can use either `--model small` or `--model micro`:
```bash
# Train Small NNUE using auto-discovered PGN games in ./data
python train.py --model small --epochs 30 --batch-size 8192 --lr 2e-3 --output ./checkpoints

# Or explicitly pass the dataset:
python train.py --model small --data ./data --epochs 30 --batch-size 8192 --output ./checkpoints
```

> [!TIP]
> Because the Small NNUE feature transformer has only 16 hidden dimensions (compared to 256 in the standard expert models), it trains and converges roughly 8–10x faster per epoch. A larger batch size of `8192` with an initial learning rate of `2e-3` is recommended.

---

## Deploying Models to the C# Engine

There are two seamless ways to get your best trained models into Anak's C# engine:

### 1. Automatic Deployment (Default)
When training finishes, `train.py` **automatically** quantizes the best checkpoint and deploys it directly into `networks/` (`open.nnue`, `closed.nnue`, or `micro.nnue`).
No extra steps are required!

*(To disable automatic deployment during experiment sweeps, pass `--no-deploy`).*

### 2. One-Click Batch Deployment Script (`deploy_best.py`)
To deploy or update all best trained checkpoints at once with a single command:

```bash
# Run from inside training/
python deploy_best.py

# Or run from repository root
python training/deploy_best.py
```
This script automatically:
- Searches all checkpoint directories (`checkpoints/` and `training/checkpoints/`).
- Discovers the newest best checkpoints (`open_best.pt`, `closed_best.pt`, `small_best.pt` / `micro_best.pt`).
- Quantizes each model to INT8/INT16 binary format.
- Deploys them into `networks/` (`open.nnue`, `closed.nnue`, and `micro.nnue`).
- Synchronizes with any active build output directories (`bin/Release/net11.0/networks/`).

### 3. Manual Single-Model Export (`export.py`)
If you want to manually quantize a specific checkpoint file:

```bash
# Export Open-NNUE
python export.py --model open --checkpoint ./checkpoints/open_best.pt --output ../networks/open.nnue

# Export Closed-NNUE
python export.py --model closed --checkpoint ./checkpoints/closed_best.pt --output ../networks/closed.nnue

# Export Small / Micro-NNUE
python export.py --model small --checkpoint ./checkpoints/small_best.pt --output ../networks/micro.nnue
```

---

## Generating Synthetic Baseline Networks for Development

To generate test baseline `.nnue` files without downloading full TCEC archives:

```bash
python generate_synthetic.py
```
This writes initial valid `networks/open.nnue`, `networks/closed.nnue`, and `networks/micro.nnue` files directly into the `networks/` folder.
