"""
Training script for Anak NNUE models on Preprocessed Binary Packs or Multi-Source Datasets.
Supports training Open, Closed, and Micro networks with Cosine Annealing, GPU scatter,
memory-mapped zero-copy streaming (>1,000,000 pos/sec), and early checkpointing.
"""

import argparse
import os
import sys
import time
from typing import List, Optional
import torch
from torch.utils.data import DataLoader
from tqdm import tqdm

from model import StandardNNUE, MicroNNUE, ModelEMA, clamp_nnue_weights
from dataset import TcecDataset, find_pgn_files
from loss import ModernNNUELoss, TcecBlendedLoss, BlunderLoss
from fast_loader import FastMemoryMappedBatchLoader
from query import PositionQuery


def resolve_data_files(data_arg: Optional[str] = None, source: str = "all") -> List[str]:
    """
    Intelligently locates PGN training data files across workspace/relative directories,
    and filters by source (tcec, generated, lichess, stockfish, leela, all).
    """
    script_dir = os.path.dirname(os.path.abspath(__file__))
    cwd = os.getcwd()

    raw_files = []
    if data_arg:
        candidates = [
            data_arg,
            os.path.join(cwd, data_arg),
            os.path.join(script_dir, data_arg),
        ]
        parent = os.path.dirname(data_arg)
        if parent and parent not in ("/", "\\", ".") and not (len(parent) <= 3 and ":" in parent):
            candidates.extend([
                parent,
                os.path.join(cwd, parent),
                os.path.join(script_dir, parent),
            ])

        for c in candidates:
            if os.path.exists(c):
                files = find_pgn_files(c)
                if files:
                    raw_files = files
                    break
    else:
        # Fallback to standard locations when --data is omitted
        default_dirs = [
            os.path.join(cwd, "training", "data"),
            os.path.join(cwd, "data"),
            os.path.join(script_dir, "data"),
            os.path.join(os.path.dirname(script_dir), "training", "data"),
        ]

        for d in default_dirs:
            if os.path.exists(d):
                files = find_pgn_files(d)
                if files:
                    raw_files = files
                    break

    if not raw_files or source.lower() in ("all", "*"):
        return raw_files

    # Filter by source tags
    sources = [s.strip().lower() for s in source.split(",") if s.strip()]
    filtered = []
    for f in raw_files:
        basename = os.path.basename(f).lower()
        matched = False
        for s in sources:
            if s == "generated" and ("generated" in basename or basename.startswith("gen_")):
                matched = True
            elif s == "tcec" and "tcec" in basename:
                matched = True
            elif s == "lichess" and "lichess" in basename:
                matched = True
            elif s in ("stockfish", "sf") and ("stockfish" in basename or basename.startswith("st_")):
                matched = True
            elif s in ("leela", "lc0") and ("leela" in basename or "lc0" in basename):
                matched = True
            elif s in basename:
                matched = True
        if matched:
            filtered.append(f)

    return filtered


def find_preprocessed_binpack(model_name: str, explicit_path: Optional[str] = None) -> Optional[str]:
    """Auto-detects preprocessed .binpack file for the target model."""
    if explicit_path:
        if os.path.isfile(explicit_path):
            return os.path.abspath(explicit_path)
        return None

    script_dir = os.path.dirname(os.path.abspath(__file__))
    model_tag = "micro" if model_name in ("micro", "small") else model_name
    candidates = [
        os.path.join(script_dir, "data", "preprocessed", f"{model_tag}.binpack"),
        os.path.join(os.getcwd(), "training", "data", "preprocessed", f"{model_tag}.binpack"),
        os.path.join(os.getcwd(), "data", "preprocessed", f"{model_tag}.binpack"),
        os.path.join(script_dir, "data", f"{model_tag}.binpack"),
    ]
    for c in candidates:
        if os.path.isfile(c):
            return os.path.abspath(c)
    return None


ALL_MODEL_CHOICES = [
    "open",
    "closed",
    "endgame",
    "queenless",
    "imbalance",
    "attack",
    "micro",
    "small",
    "volatility",
]


def is_micro_architecture(model_name: str) -> bool:
    return model_name in ("micro", "small", "volatility")


def parse_args():
    parser = argparse.ArgumentParser(description="Train Anak NNUE models on Preprocessed Binary Packs or Multi-Source Datasets")
    parser.add_argument("--model", type=str, choices=ALL_MODEL_CHOICES, default="open",
                        help="Target network to train (open, closed, endgame, queenless, imbalance, attack, micro/small, volatility)")
    parser.add_argument("--binpack", type=str, default=None,
                        help="Path to preprocessed .binpack file (auto-detected in training/data/preprocessed/ if omitted)")
    parser.add_argument("--source", type=str, default="all",
                        help="Data source filter when training from raw PGNs (tcec, generated, lichess, stockfish, leela, all)")
    parser.add_argument("--data", type=str, default=None,
                        help="Path to raw PGN file or data directory (defaults to auto-discovering ./data or training/data)")
    parser.add_argument("--query", type=str, default=None,
                        help="Optional metric query filter (e.g. 'pieces <= 16 and not has_queens')")
    parser.add_argument("--epochs", type=int, default=40, help="Number of training epochs")
    parser.add_argument("--batch-size", type=int, default=8192, help="Batch size (default: 8192 for high-speed GPU training)")
    parser.add_argument("--lr", type=float, default=1e-3, help="Initial learning rate")
    parser.add_argument("--lambda-wdl", type=float, default=0.40, help="WDL loss weight factor")
    parser.add_argument("--output", type=str, default="./checkpoints", help="Output directory for checkpoints")
    parser.add_argument("--max-samples", type=int, default=None, help="Limit number of samples per epoch")
    parser.add_argument("--deploy", action="store_true", default=True,
                        help="Automatically quantize and deploy best checkpoint to networks/ upon completion (default: True)")
    parser.add_argument("--no-deploy", dest="deploy", action="store_false",
                        help="Disable automatic deployment to networks/")
    return parser.parse_args()


def main():
    args = parse_args()
    os.makedirs(args.output, exist_ok=True)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")

    # 1. Check for Preprocessed Binary Pack
    binpack_file = find_preprocessed_binpack(args.model, explicit_path=args.binpack)
    use_fast_loader = binpack_file is not None

    print("=" * 70)
    print(f"=== Training Anak NNUE: {args.model.upper()} Model on {device} ===")
    print("=" * 70)

    if use_fast_loader:
        print(f"--> High-Speed Mode: Using preprocessed memory-mapped binpack!")
        print(f"Binpack Path:     {binpack_file} ({os.path.getsize(binpack_file):,} bytes)")
        print(f"Loading Mode:     Direct NumPy memmap + GPU sparse scatter")
    else:
        print(f"Notice: Preprocessed binpack not found for {args.model.upper()}.")
        print("Tip: Run one-time preprocessing to boost training speed by 100x-500x:")
        print("  python preprocess.py --input ./training/data --output ./training/data/preprocessed")
        print("Falling back to raw PGN streaming...")
        data_files = resolve_data_files(args.data, source=args.source)
        if not data_files:
            print("\nERROR: No matching training data or binpack found!")
            sys.exit(1)
        print(f"Found {len(data_files)} raw PGN file(s):")
        for f in data_files[:5]:
            print(f"  - {f} ({os.path.getsize(f):,} bytes)")

    print(f"Batch Size:       {args.batch_size:,}")
    print(f"Epochs:           {args.epochs}")
    print(f"Learning Rate:    {args.lr}")
    print("=" * 70)

    # 2. Instantiate Model & Loss
    if is_micro_architecture(args.model):
        model = MicroNNUE().to(device)
        criterion = BlunderLoss()
    else:
        model = StandardNNUE().to(device)
        criterion = ModernNNUELoss(lambda_wdl=args.lambda_wdl, eval_scale=2.50)

    ema = ModelEMA(model, decay=0.995)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=args.epochs, eta_min=args.lr * 0.05)

    best_loss = float("inf")
    effective_batch_size = args.batch_size
    if use_fast_loader:
        from binpack import BinpackReader
        r = BinpackReader(binpack_file)
        effective_batch_size = min(args.batch_size, max(256, len(r) // 32))
        r.close()

    # 3. Training Loop
    for epoch in range(1, args.epochs + 1):
        epoch_start = time.time()
        model.train()
        total_loss = 0.0
        batch_count = 0
        total_positions = 0

        if use_fast_loader:
            # Ultra-fast zero-copy batch streaming
            loader = FastMemoryMappedBatchLoader(
                binpack_file,
                batch_size=effective_batch_size,
                shuffle=True,
                device=device
            )
            pbar = tqdm(loader, desc=f"Epoch {epoch}/{args.epochs} [Mmap Binpack]")
            for batch in pbar:
                our_feat = batch["our_features"]
                their_feat = batch["their_features"]
                target_wdl = batch["target_wdl"]
                target_eval = batch["target_eval"]

                optimizer.zero_grad()
                preds = model(our_feat, their_feat)

                if is_micro_architecture(args.model):
                    loss = criterion(preds, target_eval)
                else:
                    loss = criterion(preds, target_wdl, target_eval)

                loss.backward()
                torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=1.0)
                optimizer.step()

                clamp_nnue_weights(model, max_val=1.98)
                ema.update(model)

                total_loss += loss.item()
                batch_count += 1
                total_positions += our_feat.shape[0]
                pbar.set_postfix({"loss": f"{loss.item():.4f}"})

                if args.max_samples and total_positions >= args.max_samples:
                    break

            loader.close()

        else:
            # Fallback PGN streaming
            dataset = TcecDataset(
                pgn_path=data_files,
                target_model=args.model,
                max_samples=args.max_samples
            )
            dataloader = DataLoader(dataset, batch_size=args.batch_size, num_workers=0)
            pbar = tqdm(dataloader, desc=f"Epoch {epoch}/{args.epochs} [PGN Stream]")
            for batch in pbar:
                our_feat = batch["our_features"].to(device)
                their_feat = batch["their_features"].to(device)
                target_wdl = batch["target_wdl"].to(device)
                target_eval = batch["target_eval"].to(device)

                optimizer.zero_grad()
                preds = model(our_feat, their_feat)

                if is_micro_architecture(args.model):
                    loss = criterion(preds, target_eval)
                else:
                    loss = criterion(preds, target_wdl, target_eval)

                loss.backward()
                torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=1.0)
                optimizer.step()

                clamp_nnue_weights(model, max_val=1.98)
                ema.update(model)

                total_loss += loss.item()
                batch_count += 1
                total_positions += our_feat.shape[0]
                pbar.set_postfix({"loss": f"{loss.item():.4f}"})

        scheduler.step()
        epoch_time = time.time() - epoch_start
        rate = total_positions / max(0.001, epoch_time)
        avg_loss = total_loss / max(1, batch_count)

        print(f"Epoch {epoch} Done | Avg Loss: {avg_loss:.4f} | Throughput: {rate:,.0f} pos/sec ({epoch_time:.1f}s) | LR: {scheduler.get_last_lr()[0]:.6f}")

        # Checkpointing
        names = ["micro", "small"] if args.model in ("micro", "small") else [args.model]
        for name in names:
            checkpoint_path = os.path.join(args.output, f"{name}_latest.pt")
            torch.save(model.state_dict(), checkpoint_path)

        if avg_loss < best_loss:
            best_loss = avg_loss
            backup = ema.store_and_apply(model)
            for name in names:
                best_path = os.path.join(args.output, f"{name}_best.pt")
                torch.save(model.state_dict(), best_path)
            ema.restore(model, backup)
            print(f"--> Saved new best checkpoint (EMA): {os.path.join(args.output, f'{args.model}_best.pt')}")

    print("Training finished successfully!")

    # 4. Automatic Model Deployment
    if args.deploy and best_loss < float("inf"):
        print("\n--> Auto-deploying best checkpoint to networks/...")
        try:
            from deploy_best import deploy_model, find_repo_root
            repo_root = find_repo_root()
            net_dir = os.path.join(repo_root, "networks")
            out_name = "micro.nnue" if args.model in ("micro", "small") else f"{args.model}.nnue"
            target_path = os.path.join(net_dir, out_name)
            best_ckpt = os.path.join(args.output, f"{args.model}_best.pt")
            deploy_model(args.model, best_ckpt, target_path)
            print(f"--> Deployed to {target_path} successfully!")
        except Exception as e:
            print(f"Notice: Auto-deploy skipped ({e})")


if __name__ == "__main__":
    main()
