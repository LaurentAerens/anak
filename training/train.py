"""
Training script for Anak NNUE models on Preprocessed Binary Packs or Multi-Source Datasets.
Supports:
- PyTorch Mixed Precision (AMP: bfloat16 / float16 + GradScaler) for 2x-3x speedup on NVIDIA GPUs
- Sharded / multi-file memory-mapped binpack streaming (>1,000,000 pos/sec)
- Linear learning rate warmup (2-3 epochs) followed by Cosine Annealing
- 5% validation split tracking validation loss to prevent overfitting
- Batch sizes scaling cleanly up to 16,384 or 32,768+
"""

import argparse
import math
import os
import sys
import time
from typing import List, Optional, Union
import torch
from torch.utils.data import DataLoader
from tqdm import tqdm

from model import StandardNNUE, MicroNNUE, ModelEMA, clamp_nnue_weights, enforce_perspective_antisymmetry
from dataset import TcecDataset, find_pgn_files
from loss import ModernNNUELoss, TcecBlendedLoss, BlunderLoss
from fast_loader import FastMemoryMappedBatchLoader, resolve_binpack_files
from query import PositionQuery
from binpack import BinpackReader


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


def find_preprocessed_binpacks(model_name: str, explicit_path: Optional[str] = None) -> List[str]:
    """Auto-detects or resolves preprocessed .binpack file(s) for the target model."""
    if explicit_path:
        resolved = resolve_binpack_files(explicit_path)
        if resolved:
            return resolved
        return []

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
            return [os.path.abspath(c)]
    return []


def find_preprocessed_binpack(model_name: str, explicit_path: Optional[str] = None) -> Optional[str]:
    """Backward-compatible single-file detector."""
    files = find_preprocessed_binpacks(model_name, explicit_path)
    return files[0] if files else None


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
                        help="Path to preprocessed .binpack file, directory of shards, or glob pattern (e.g. 'data/*.binpack')")
    parser.add_argument("--source", type=str, default="all",
                        help="Data source filter when training from raw PGNs (tcec, generated, lichess, stockfish, leela, all)")
    parser.add_argument("--data", type=str, default=None,
                        help="Path to raw PGN file or data directory (defaults to auto-discovering ./data or training/data)")
    parser.add_argument("--query", type=str, default=None,
                        help="Optional metric query filter (e.g. 'pieces <= 16 and not has_queens')")
    parser.add_argument("--epochs", type=int, default=40, help="Number of training epochs")
    parser.add_argument("--warmup-epochs", type=int, default=3, help="Linear learning rate warmup epochs (default: 3)")
    parser.add_argument("--val-split", type=float, default=0.05, help="Validation data split fraction (default: 0.05 = 5 percent)")
    parser.add_argument("--batch-size", type=int, default=8192, help="Batch size (e.g. 8192, 16384, 32768 for high-speed GPU training)")
    parser.add_argument("--lr", type=float, default=1e-3, help="Initial learning rate")
    parser.add_argument("--lambda-wdl", type=float, default=0.35, help="WDL loss weight factor (default: 0.35)")
    parser.add_argument("--augment-flip", action="store_true", default=True, help="Apply random horizontal board reflection during training (default: True)")
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
    if device.type == "cpu":
        cpu_threads = os.cpu_count() or 4
        torch.set_num_threads(cpu_threads)

    # Mixed Precision (AMP) Configuration
    is_cuda = (device.type == "cuda")
    has_bf16 = is_cuda and hasattr(torch.cuda, "is_bf16_supported") and torch.cuda.is_bf16_supported()
    amp_dtype = torch.bfloat16 if has_bf16 else torch.float16
    amp_enabled = is_cuda
    scaler = torch.cuda.amp.GradScaler(enabled=(is_cuda and amp_dtype == torch.float16))

    # 1. Check for Preprocessed Binary Pack(s)
    binpack_files = find_preprocessed_binpacks(args.model, explicit_path=args.binpack)
    use_fast_loader = len(binpack_files) > 0

    print("=" * 75)
    print(f"=== Training Anak NNUE: {args.model.upper()} Model on {device} ===")
    print("=" * 75)

    if is_cuda:
        precision_str = "bfloat16 (native)" if has_bf16 else "float16 (+ GradScaler)"
        print(f"--> Mixed Precision (AMP): ENABLED ({precision_str})")
    else:
        print(f"--> Mixed Precision (AMP): DISABLED (CPU float32)")
        print(f"--> CPU Multi-Threading:   {cpu_threads} threads enabled (saturating all physical and logical cores)")
        print("--> Notice: Running on CPU (no CUDA GPU detected). Ultra-fast chunked streaming pipeline active.")

    if use_fast_loader:
        total_bytes = sum(os.path.getsize(f) for f in binpack_files)
        print(f"--> High-Speed Mode: Using {len(binpack_files)} memory-mapped binpack file(s) ({total_bytes:,} bytes)")
        for f in binpack_files[:3]:
            print(f"  - {f} ({os.path.getsize(f):,} bytes)")
        if len(binpack_files) > 3:
            print(f"  ... and {len(binpack_files) - 3} more file(s)")
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
    print(f"Epochs:           {args.epochs} (Warmup: {args.warmup_epochs} epochs)")
    print(f"Validation Split: {args.val_split * 100:.1f}%")
    print(f"Learning Rate:    {args.lr}")
    print("=" * 75)

    # 2. Instantiate Model & Loss
    if is_micro_architecture(args.model):
        model = MicroNNUE().to(device)
        criterion = BlunderLoss()
    else:
        model = StandardNNUE().to(device)
        criterion = ModernNNUELoss(lambda_wdl=args.lambda_wdl, eval_scale=2.50)

    ema = ModelEMA(model, decay=0.995)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)

    # Linear Warmup followed by Cosine Annealing
    warmup_epochs = min(args.warmup_epochs, max(0, args.epochs // 4))
    if warmup_epochs > 0 and args.epochs > warmup_epochs:
        warmup_sched = torch.optim.lr_scheduler.LinearLR(
            optimizer, start_factor=0.1, end_factor=1.0, total_iters=warmup_epochs
        )
        cosine_sched = torch.optim.lr_scheduler.CosineAnnealingLR(
            optimizer, T_max=args.epochs - warmup_epochs, eta_min=args.lr * 0.05
        )
        scheduler = torch.optim.lr_scheduler.SequentialLR(
            optimizer, schedulers=[warmup_sched, cosine_sched], milestones=[warmup_epochs]
        )
    else:
        scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(
            optimizer, T_max=args.epochs, eta_min=args.lr * 0.05
        )

    best_metric = float("inf")
    effective_batch_size = args.batch_size

    if use_fast_loader:
        total_raw = sum(len(BinpackReader(f)) for f in binpack_files)
        train_count = int(total_raw * (1.0 - args.val_split)) if args.val_split > 0 else total_raw
        effective_batch_size = min(args.batch_size, max(256, train_count))
        train_loader = FastMemoryMappedBatchLoader(
            binpack_files,
            batch_size=effective_batch_size,
            shuffle=True,
            device=device,
            val_split=args.val_split,
            is_validation=False,
            augment_flip=args.augment_flip,
        )
        val_loader = FastMemoryMappedBatchLoader(
            binpack_files,
            batch_size=effective_batch_size,
            shuffle=False,
            device=device,
            val_split=args.val_split,
            is_validation=True,
            augment_flip=False,
        ) if args.val_split > 0 else None
    else:
        train_loader = None
        val_loader = None

    # 3. Training Loop
    try:
        for epoch in range(1, args.epochs + 1):
            epoch_start = time.time()
            model.train()
            total_loss = 0.0
            batch_count = 0
            total_positions = 0

            if use_fast_loader:
                pbar = tqdm(train_loader, desc=f"Epoch {epoch}/{args.epochs} [Mmap Binpack]")
                for batch in pbar:
                    our_feat = batch["our_features"]
                    their_feat = batch["their_features"]
                    target_wdl = batch["target_wdl"]
                    target_eval = batch["target_eval"]

                    optimizer.zero_grad(set_to_none=True)
                    with torch.cuda.amp.autocast(enabled=amp_enabled, dtype=amp_dtype):
                        preds = model(our_feat, their_feat)
                        if is_micro_architecture(args.model):
                            loss = criterion(preds, target_eval)
                        else:
                            loss = criterion(preds, target_wdl, target_eval)

                    if scaler.is_enabled():
                        scaler.scale(loss).backward()
                        scaler.unscale_(optimizer)
                        torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=1.0)
                        scaler.step(optimizer)
                        scaler.update()
                    else:
                        loss.backward()
                        torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=1.0)
                        optimizer.step()

                    clamp_nnue_weights(model, max_val=1.98)
                    ema.update(model)

                    total_loss += loss.item()
                    batch_count += 1
                    total_positions += our_feat.shape[0]

                    elapsed = max(0.001, time.time() - epoch_start)
                    pos_rate = total_positions / elapsed
                    pbar.set_postfix({
                        "loss": f"{loss.item():.4f}",
                        "pos/s": f"{pos_rate:,.0f}"
                    })

                    if args.max_samples and total_positions >= args.max_samples:
                        break

                enforce_perspective_antisymmetry(model)

                # Validation evaluation
                avg_val_loss = None
                if val_loader and len(val_loader) > 0:
                    model.eval()
                    val_loss = 0.0
                    val_batches = 0
                    with torch.no_grad():
                        for v_batch in val_loader:
                            our_feat = v_batch["our_features"]
                            their_feat = v_batch["their_features"]
                            target_wdl = v_batch["target_wdl"]
                            target_eval = v_batch["target_eval"]
                            with torch.cuda.amp.autocast(enabled=amp_enabled, dtype=amp_dtype):
                                preds = model(our_feat, their_feat)
                                if is_micro_architecture(args.model):
                                    v_l = criterion(preds, target_eval)
                                else:
                                    v_l = criterion(preds, target_wdl, target_eval)
                            val_loss += v_l.item()
                            val_batches += 1
                    if val_batches > 0:
                        avg_val_loss = val_loss / val_batches
    finally:
        if train_loader:
            train_loader.close()
        if val_loader:
            val_loader.close()

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

                optimizer.zero_grad(set_to_none=True)
                with torch.cuda.amp.autocast(enabled=amp_enabled, dtype=amp_dtype):
                    preds = model(our_feat, their_feat)
                    if is_micro_architecture(args.model):
                        loss = criterion(preds, target_eval)
                    else:
                        loss = criterion(preds, target_wdl, target_eval)

                if scaler.is_enabled():
                    scaler.scale(loss).backward()
                    scaler.unscale_(optimizer)
                    torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=1.0)
                    scaler.step(optimizer)
                    scaler.update()
                else:
                    loss.backward()
                    torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=1.0)
                    optimizer.step()

                clamp_nnue_weights(model, max_val=1.98)
                ema.update(model)

                total_loss += loss.item()
                batch_count += 1
                total_positions += our_feat.shape[0]
                pbar.set_postfix({"loss": f"{loss.item():.4f}"})

            avg_val_loss = None

        scheduler.step()
        epoch_time = time.time() - epoch_start
        rate = total_positions / max(0.001, epoch_time)
        avg_loss = total_loss / max(1, batch_count)

        val_str = f"{avg_val_loss:.4f}" if avg_val_loss is not None else "N/A"
        print(f"Epoch {epoch:2d}/{args.epochs} | Train Loss: {avg_loss:.4f} | Val Loss: {val_str} | Throughput: {rate:,.0f} pos/sec ({epoch_time:.2f}s) | LR: {scheduler.get_last_lr()[0]:.6f}")

        # Checkpointing: track validation loss if available, otherwise training loss
        eval_metric = avg_val_loss if avg_val_loss is not None else avg_loss

        names = ["micro", "small"] if args.model in ("micro", "small") else [args.model]
        for name in names:
            checkpoint_path = os.path.join(args.output, f"{name}_latest.pt")
            torch.save(model.state_dict(), checkpoint_path)

        if eval_metric < best_metric:
            best_metric = eval_metric
            backup = ema.store_and_apply(model)
            for name in names:
                best_path = os.path.join(args.output, f"{name}_best.pt")
                torch.save(model.state_dict(), best_path)
            ema.restore(model, backup)
            metric_tag = "Val Loss" if avg_val_loss is not None else "Train Loss"
            print(f"--> Saved new best checkpoint (EMA) [{metric_tag}: {eval_metric:.4f}]: {os.path.join(args.output, f'{args.model}_best.pt')}")

    print("Training finished successfully!")

    # 4. Automatic Model Deployment
    if args.deploy and best_metric < float("inf"):
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
