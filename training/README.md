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

### 1. Train the Open-NNUE Model
```bash
python train.py --model open --data data/tcec.pgn.gz --epochs 40 --batch-size 4096 --output ./checkpoints
```

### 2. Train the Closed-NNUE Model
```bash
python train.py --model closed --data data/tcec.pgn.gz --epochs 40 --batch-size 4096 --output ./checkpoints
```

### 3. Train the Micro-NNUE Blunder Watchdog
```bash
python train.py --model micro --data data/tcec.pgn.gz --epochs 30 --batch-size 8192 --output ./checkpoints
```

---

## Exporting Quantized Binaries for Anak

Export the trained PyTorch checkpoints into Anak's zero-GC binary format:

```bash
# Export Open-NNUE
python export.py --model open --checkpoint ./checkpoints/open_best.pt --output ../networks/open.nnue

# Export Closed-NNUE
python export.py --model closed --checkpoint ./checkpoints/closed_best.pt --output ../networks/closed.nnue

# Export Micro-NNUE
python export.py --model micro --checkpoint ./checkpoints/micro_best.pt --output ../networks/micro.nnue
```

---

## Generating Synthetic Baseline Networks for Development

To generate test baseline `.nnue` files without downloading full TCEC archives:

```bash
python generate_synthetic.py
```
This writes `networks/open.nnue`, `networks/closed.nnue`, and `networks/micro.nnue` directly into the `networks/` folder.
