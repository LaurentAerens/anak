"""
All-in-One Master Training Pipeline for Anak NNUE:
1. Downloads the official Stockfish 500M Depth-9 (d9) training database (500M_d9_p1.binpack & 500M_d9_mix_p1.binpack)
2. Executes native C++20 BINP unpacking & global shuffling into open.binpack, closed.binpack, and micro.binpack
   (default: 4,800,000 Stockfish d9 positions -> 300,000 globally shuffled positions per expert model = 900,000 total)
3. Trains Open-NNUE, Closed-NNUE, and Micro-NNUE with 100% trainable weights, perspective-twin antisymmetry,
   step-aware ModelEMA warmup, and ModernNNUELoss / BlunderLoss
4. Verifies C# quantization parity and deploys all 3 models to networks/ for Anak's C# engine.
"""

import argparse
import math
import os
import random
import shutil
import sys
import time
from typing import List, Union, Optional
import chess
import numpy as np
import torch
from tqdm import tqdm

from model import (
    StandardNNUE,
    MicroNNUE,
    ModelEMA,
    clamp_nnue_weights,
    enforce_perspective_antisymmetry,
    evaluate_benchmark_positions,
)
from loss import ModernNNUELoss, BlunderLoss
from fast_loader import FastMemoryMappedBatchLoader, resolve_binpack_files
from binpack import BinpackReader, BinpackWriter, read_binpack_count
from preprocess import preprocess_data
from export import export_standard, export_micro
from dataset import find_pgn_files, encode_board_features, calculate_openness
from download_data import download_stockfish_d9_binpacks, convert_stockfish_d9_to_anak, STOCKFISH_D9_URLS
from generate_data import BuiltinTacticalEvaluator, load_seed_openings
from hardware import auto_tune_hardware, HardwareProfiler


def print_banner(text: str):
    print("\n" + "=" * 75, flush=True)
    print(f"  {text}", flush=True)
    print("=" * 75, flush=True)


ALL_8_MODELS = [
    "open",
    "closed",
    "endgame",
    "queenless",
    "imbalance",
    "attack",
    "micro",
    "volatility",
]


def is_micro_model(model_name: str) -> bool:
    return model_name in ("micro", "small", "volatility")


def ensure_stockfish_d9_binpacks(
    raw_data_dir: str,
    preprocessed_dir: str,
    min_records_per_pack: int = 5_000_000,
    chunks_per_file: int = 100,
    required_models: List[str] = None,
    force_rebuild: bool = False,
    include_d11: bool = True,
):
    """
    Ensures all expert/helper .binpack files in preprocessed_dir exist and are populated
    from the Stockfish Depth-9 & Depth-11 multi-hundred-million evaluation database.
    """
    os.makedirs(preprocessed_dir, exist_ok=True)
    target_models = required_models or ALL_8_MODELS

    needs_build = force_rebuild
    if not needs_build:
        for m in target_models:
            tag = "micro" if m == "small" else m
            path = os.path.join(preprocessed_dir, f"{tag}.binpack")
            count = read_binpack_count(path)
            # Specialized expert categories (endgame, queenless, imbalance, attack, volatility)
            # scale with min_records_per_pack // 2, with no artificial 50k ceiling!
            min_req = min_records_per_pack if tag in ("open", "closed", "micro") else max(250_000, min_records_per_pack // 2)
            if count < min_req:
                print(f"  [Check] {tag}.binpack has {count:,} finalized records (target >= {min_req:,}) -> rebuild needed.", flush=True)
                needs_build = True
                break

    if not needs_build:
        print_banner("STEP 1 & 2: STOCKFISH DEPTH-9/11 8-EXPERT BINPACKS READY")
        for m in ALL_8_MODELS:
            path = os.path.join(preprocessed_dir, f"{m}.binpack")
            count = read_binpack_count(path)
            if count > 0:
                print(f"  - {m.capitalize():10s} Binpack: {count:,} positions ({os.path.getsize(path):,} bytes)", flush=True)
        return

    print_banner("STEP 1: DOWNLOADING STOCKFISH 500M DEPTH-9 & DEPTH-11 TRAINING ARCHIVES")
    urls_to_pull = STOCKFISH_D9_URLS if include_d11 else [u for u in STOCKFISH_D9_URLS if "_d11" not in u[0]]
    required_chunks = max(chunks_per_file, math.ceil((min_records_per_pack * 2.0) / 200_000)) if chunks_per_file > 0 else 0
    sf_files = download_stockfish_d9_binpacks(raw_data_dir, chunks_per_file=required_chunks, dataset_urls=urls_to_pull)

    print_banner("STEP 2: NATIVE MULTI-THREADED UNPACKING & SHUFFLING INTO 8 ANAK BINPACKS")
    convert_stockfish_d9_to_anak(
        sf_binpack_files=sf_files,
        output_dir=preprocessed_dir,
        max_per_pack=min_records_per_pack
    )


def verify_model_sanity(model: torch.nn.Module, model_name: str):
    """Evaluates standard material and tactical benchmark positions to verify model sanity."""
    device = next(model.parameters()).device
    model.eval()
    test_positions = [
        ("StartPos", chess.STARTING_FEN),
        ("White +Queen", "rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"),
        ("White -Queen", "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNB1KBNR w KQkq - 0 1"),
        ("White +Rook", "1nbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQk - 0 1"),
        ("White -Rook", "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/1NBQKBNR w Kkq - 0 1"),
        ("White +Knight", "r1bqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"),
    ]
    results = {}
    with torch.no_grad():
        for label, fen in test_positions:
            b = chess.Board(fen)
            our_idx, their_idx = encode_board_features(b)
            our_t = torch.zeros((1, 768), dtype=torch.float32, device=device)
            their_t = torch.zeros((1, 768), dtype=torch.float32, device=device)
            our_t[0, our_idx] = 1.0
            their_t[0, their_idx] = 1.0
            pred_pawns = model(our_t, their_t).item()
            results[label] = pred_pawns

    summary = " | ".join(f"{k}: {v*100:+.0f}cp" for k, v in results.items())
    print(f"  [Sanity Check - {model_name.upper()}] {summary}")
    return results


def train_single_model(
    model_name: str,
    binpack_path: Union[str, List[str]],
    epochs: int,
    batch_size: int,
    lr: float,
    device: torch.device,
    checkpoints_dir: str,
    warmup_epochs: int = 3,
    val_split: float = 0.05,
    augment_flip: bool = True,
    lambda_wdl: float = 0.35,
    patience: int = 15,
    min_delta: float = 1e-4,
    mix_sources: Optional[Union[str, List[str]]] = None,
    mix_ratio: float = 0.20,
    compile_model: bool = False,
    workers: Optional[int] = None,
    prefetch_batches: Optional[int] = None,
    chunk_size: Optional[int] = None,
) -> str:
    """
    Trains one NNUE model on its preprocessed Stockfish d9/d11 binpack using 100% trainable weights,
    PyTorch Mixed Precision (AMP: bfloat16/float16), 80/20 domain mixture regularization,
    horizontal board reflection data augmentation, perspective-twin antisymmetry, linear warmup +
    Cosine Annealing, validation loss tracking, step-aware ModelEMA, early stopping, and ModernNNUELoss / BlunderLoss.
    Returns path to best checkpoint (.pt).
    """
    print_banner(f"TRAINING {model_name.upper()}-NNUE ({epochs} Epochs on {device})")
    binpack_files = resolve_binpack_files(binpack_path)
    if not binpack_files:
        raise FileNotFoundError(f"No binpack files found for {binpack_path}")

    total_records = sum(len(BinpackReader(f)) for f in binpack_files)
    train_count = int(total_records * (1.0 - val_split)) if val_split > 0 else total_records
    effective_batch_size = min(batch_size, max(256, train_count))

    total_bytes = sum(os.path.getsize(f) for f in binpack_files)
    print(f"Source Binpack(s): {len(binpack_files)} file(s), {total_records:,} positions ({total_bytes:,} bytes)")
    print(f"Batch Size:        {effective_batch_size:,} ({math.ceil(train_count / effective_batch_size):,} train steps/epoch)")
    print(f"Validation Split:  {val_split * 100:.1f}% ({total_records - train_count:,} validation positions)")
    print(f"Data Augmentation: {'Horizontal Board Mirroring (sq ^ 7)' if augment_flip else 'Disabled'}")
    if mix_sources and mix_ratio > 0.0:
        mix_pct = int(mix_ratio * 100)
        primary_pct = 100 - mix_pct
        print(f"Domain Mixture:    {primary_pct}% {model_name.upper()} + {mix_pct}% Global Cross-Domain Mix")
    else:
        print("Domain Mixture:    100% Domain-Specific")

    # Mixed Precision (AMP) Configuration
    is_cuda = (device.type == "cuda")
    has_bf16 = is_cuda and hasattr(torch.cuda, "is_bf16_supported") and torch.cuda.is_bf16_supported()
    amp_dtype = torch.bfloat16 if has_bf16 else torch.float16
    amp_enabled = is_cuda
    scaler = torch.amp.GradScaler("cuda", enabled=(is_cuda and amp_dtype == torch.float16))

    if is_cuda:
        precision_str = "bfloat16 (native Tensor Cores)" if has_bf16 else "float16 (+ GradScaler)"
        print(f"Mixed Precision:   ENABLED ({precision_str})")
        if hasattr(torch, "set_float32_matmul_precision"):
            torch.set_float32_matmul_precision("high")
        if hasattr(torch.backends, "cudnn") and torch.backends.cudnn.is_available():
            torch.backends.cudnn.benchmark = True
    else:
        print("Mixed Precision:   DISABLED (CPU float32)")

    if is_micro_model(model_name):
        model = MicroNNUE().to(device)
        criterion = BlunderLoss(blunder_weight=5.0)
    else:
        model = StandardNNUE().to(device)
        criterion = ModernNNUELoss(lambda_wdl=lambda_wdl, eval_scale=2.50)

    if compile_model and is_cuda and hasattr(torch, "compile"):
        try:
            print("--> PyTorch 2.x torch.compile: ENABLED (Triton kernel fusion)")
            model = torch.compile(model, mode="reduce-overhead")
        except Exception as e:
            print(f"Notice: torch.compile skipped ({e})")

    is_micro = is_micro_model(model_name)
    ema = ModelEMA(model, decay=0.990 if is_micro else 0.999)

    # Fused AdamW optimizer on CUDA (single kernel launch for all parameters and momentum buffers)
    adamw_kwargs = {"lr": lr, "weight_decay": 0.0 if is_micro else 1e-5}
    if is_cuda:
        try:
            optimizer = torch.optim.AdamW(model.parameters(), fused=True, **adamw_kwargs)
        except Exception:
            optimizer = torch.optim.AdamW(model.parameters(), **adamw_kwargs)
    else:
        optimizer = torch.optim.AdamW(model.parameters(), **adamw_kwargs)

    # Linear Warmup followed by Cosine Annealing
    actual_warmup = min(warmup_epochs, max(0, epochs // 4))
    if actual_warmup > 0 and epochs > actual_warmup:
        warmup_sched = torch.optim.lr_scheduler.LinearLR(
            optimizer, start_factor=0.1, end_factor=1.0, total_iters=actual_warmup
        )
        cosine_sched = torch.optim.lr_scheduler.CosineAnnealingLR(
            optimizer, T_max=epochs - actual_warmup, eta_min=lr * 0.01
        )
        scheduler = torch.optim.lr_scheduler.SequentialLR(
            optimizer, schedulers=[warmup_sched, cosine_sched], milestones=[actual_warmup]
        )
    else:
        scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=epochs, eta_min=lr * 0.01)

    best_metric = float("inf")
    epochs_without_improvement = 0
    out_tag = "micro" if model_name == "small" else model_name
    best_path = os.path.join(checkpoints_dir, f"{out_tag}_best.pt")
    latest_path = os.path.join(checkpoints_dir, f"{out_tag}_latest.pt")

    train_loader = FastMemoryMappedBatchLoader(
        binpack_files,
        batch_size=effective_batch_size,
        shuffle=True,
        device=device,
        val_split=val_split,
        is_validation=False,
        augment_flip=augment_flip,
        mix_sources=mix_sources,
        mix_ratio=mix_ratio,
        prefetch_batches=prefetch_batches or (16 if is_cuda else 4),
        num_workers=workers,
        chunk_size=chunk_size or 65536,
    )
    val_batch_size = (effective_batch_size * 2) if is_cuda else effective_batch_size
    val_loader = FastMemoryMappedBatchLoader(
        binpack_files,
        batch_size=val_batch_size,
        shuffle=False,
        device=device,
        val_split=val_split,
        is_validation=True,
        augment_flip=False,
        prefetch_batches=2,
        num_workers=2,
        chunk_size=chunk_size or 65536,
    ) if val_split > 0 else None

    try:
        for epoch in range(1, epochs + 1):
            epoch_start = time.time()
            model.train()
            total_loss = 0.0
            batch_count = 0
            total_positions = 0

            pbar = tqdm(train_loader, desc=f"{model_name.capitalize()} Epoch {epoch}/{epochs}", leave=False)
            for batch in pbar:
                our_in = batch.get("our_indices", batch["our_features"]).to(device, non_blocking=is_cuda)
                their_in = batch.get("their_indices", batch["their_features"]).to(device, non_blocking=is_cuda)
                target_wdl = batch["target_wdl"].to(device, non_blocking=is_cuda)
                target_eval = batch["target_eval"].to(device, non_blocking=is_cuda)

                optimizer.zero_grad(set_to_none=True)
                with torch.amp.autocast("cuda" if is_cuda else "cpu", enabled=amp_enabled, dtype=amp_dtype):
                    preds = model(our_in, their_in)
                    if is_micro_model(model_name):
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
                total_positions += our_in.shape[0]

                elapsed = max(0.001, time.time() - epoch_start)
                pos_rate = total_positions / elapsed
                pbar.set_postfix({"loss": f"{loss.item():.4f}", "pos/s": f"{pos_rate:,.0f}"})

            # Enforce perspective antisymmetry after each epoch
            enforce_perspective_antisymmetry(model)

            # Accelerated validation with torch.inference_mode
            avg_val_loss = None
            if val_loader and len(val_loader) > 0:
                model.eval()
                val_loss = 0.0
                val_batches = 0
                with torch.inference_mode():
                    for v_batch in val_loader:
                        v_our_in = v_batch.get("our_indices", v_batch["our_features"]).to(device, non_blocking=is_cuda)
                        v_their_in = v_batch.get("their_indices", v_batch["their_features"]).to(device, non_blocking=is_cuda)
                        v_target_wdl = v_batch["target_wdl"].to(device, non_blocking=is_cuda)
                        v_target_eval = v_batch["target_eval"].to(device, non_blocking=is_cuda)
                        with torch.amp.autocast("cuda" if is_cuda else "cpu", enabled=amp_enabled, dtype=amp_dtype):
                            preds = model(v_our_in, v_their_in)
                            if is_micro_model(model_name):
                                v_l = criterion(preds, v_target_eval)
                            else:
                                v_l = criterion(preds, v_target_wdl, v_target_eval)
                        val_loss += v_l.item()
                        val_batches += 1
                if val_batches > 0:
                    avg_val_loss = val_loss / val_batches

            scheduler.step()

            epoch_time = time.time() - epoch_start
            throughput = total_positions / max(0.001, epoch_time)
            avg_loss = total_loss / max(1, batch_count)
            val_str = f"{avg_val_loss:.4f}" if avg_val_loss is not None else "N/A"

            print(f"  Epoch {epoch:2d}/{epochs} | Train Loss: {avg_loss:.4f} | Val Loss: {val_str} | Throughput: {throughput:,.0f} pos/sec ({epoch_time:.2f}s) | LR: {scheduler.get_last_lr()[0]:.6f}")

            # Canonical benchmark FEN sanity check
            try:
                fen_summary = evaluate_benchmark_positions(model, device)
                print(f"    [FEN Sanity Check] {fen_summary}")
            except Exception:
                pass

            eval_metric = avg_val_loss if avg_val_loss is not None else avg_loss
            if eval_metric < (best_metric - min_delta):
                best_metric = eval_metric
                epochs_without_improvement = 0
                backup = ema.store_and_apply(model)
                enforce_perspective_antisymmetry(model)
                clamp_nnue_weights(model)
                torch.save(model.state_dict(), best_path)

                # Auto-export quantized .nnue binary to networks/
                try:
                    repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
                    net_dir = os.path.join(repo_root, "networks")
                    os.makedirs(net_dir, exist_ok=True)
                    out_nnue_name = f"{out_tag}.nnue"
                    nnue_dest = os.path.join(net_dir, out_nnue_name)
                    if is_micro_model(model_name):
                        export_micro(model, nnue_dest)
                    else:
                        export_standard(model, nnue_dest)
                except Exception:
                    pass

                ema.restore(model, backup)
            else:
                epochs_without_improvement += 1

            # Always save latest checkpoint
            torch.save(model.state_dict(), latest_path)

            # Early stopping check
            if patience > 0 and epochs_without_improvement >= patience:
                print(f"  [Early Stopping] No improvement in validation loss for {patience} epochs. Halting early at epoch {epoch}/{epochs}.")
                break
    finally:
        train_loader.close()
        if val_loader:
            val_loader.close()

    if os.path.isfile(best_path):
        model.load_state_dict(torch.load(best_path, map_location=device))
    enforce_perspective_antisymmetry(model)
    clamp_nnue_weights(model)
    torch.save(model.state_dict(), best_path)
    verify_model_sanity(model, model_name)

    metric_tag = "Best Val Loss" if avg_val_loss is not None else "Best Loss"
    print(f"--> Finished {model_name.upper()}! {metric_tag}: {best_metric:.4f} -> {best_path}")
    return best_path


def parse_models_arg(models_arg: List[str]) -> List[str]:
    if not models_arg or "all" in [m.lower() for m in models_arg]:
        return list(ALL_8_MODELS)
    selected = []
    for item in models_arg:
        for part in item.split(","):
            m = part.strip().lower()
            if not m:
                continue
            if m == "small":
                m = "micro"
            if m in ALL_8_MODELS and m not in selected:
                selected.append(m)
            elif m not in ALL_8_MODELS:
                raise ValueError(f"Unknown model '{m}'. Valid choices: {ALL_8_MODELS + ['small', 'all']}")
    return selected or list(ALL_8_MODELS)


def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)
    default_networks = os.path.join(repo_root, "networks")

    parser = argparse.ArgumentParser(description="All-in-One Anak 8-Model NNUE Pipeline: Stockfish d9/d11 Gather, Unpack, Train, Deploy")
    parser.add_argument(
        "--models",
        type=str,
        nargs="+",
        default=["all"],
        help="Models to train (default: all 8 models: open, closed, endgame, queenless, imbalance, attack, micro, volatility)",
    )
    parser.add_argument(
        "--preset",
        type=str,
        choices=["quick", "standard", "large", "massive"],
        default=None,
        help="Convenience preset: 'quick' (500k pos, 30 ep), 'standard' (5M pos, 60 ep), 'large' (15M pos, 80 ep), 'massive' (30M+ pos, 100 ep, full d9/d11)",
    )
    parser.add_argument("--auto", action="store_true", default=True, help="Automatically scan hardware and pin optimal settings (default: True)")
    parser.add_argument("--no-auto", dest="auto", action="store_false", help="Disable automatic hardware tuning")
    parser.add_argument("--compile", action="store_true", default=None, help="Enable PyTorch 2.x torch.compile for CUDA kernel fusion on modern GPUs (e.g. RTX 4090)")
    parser.add_argument("--epochs", type=int, default=80, help="Number of epochs per model (default: 80)")
    parser.add_argument("--warmup-epochs", type=int, default=3, help="Linear learning rate warmup epochs (default: 3)")
    parser.add_argument("--val-split", type=float, default=0.05, help="Validation data split fraction (default: 0.05 = 5 percent)")
    parser.add_argument("--batch-size", type=int, default=None, help="Batch size (auto-tuned if omitted: 32768 on 24GB GPU, 8192 on CPU)")
    parser.add_argument("--workers", type=int, default=None, help="Parallel worker threads for data loading (auto-tuned if omitted)")
    parser.add_argument("--prefetch-batches", type=int, default=None, help="Number of prefetch batches in RAM (auto-tuned if omitted: 16-24 on GPU)")
    parser.add_argument("--chunk-size", type=int, default=None, help="Sequential disk read chunk size (auto-tuned if omitted: 65536 - 131072)")
    parser.add_argument("--lr", type=float, default=1e-3, help="Learning rate (default: 1e-3)")
    parser.add_argument("--lambda-wdl", type=float, default=0.35, help="WDL weight in ModernNNUELoss (default: 0.35)")
    parser.add_argument("--mix-ratio", type=float, default=0.20, help="Fraction of batch sampled from other expert datasets (default: 0.20 = 80 percent target, 20 percent mix)")
    parser.add_argument("--patience", type=int, default=15, help="Early stopping patience in epochs (default: 15, 0 = disabled)")
    parser.add_argument("--min-delta", type=float, default=1e-4, help="Minimum validation loss delta to qualify as an improvement for early stopping (default: 1e-4)")
    parser.add_argument("--positions-per-model", type=int, default=5_000_000, help="Target positions per expert binpack (default: 5,000,000; supports 30,000,000+)")
    parser.add_argument("--d9-chunks", type=int, default=150, help="Number of 200k-position BINP chunks per file (default: 150; set 0 for full 500M files)")
    parser.add_argument("--full-download", action="store_true", default=False, help="Download full 500M archives without chunk limit")
    parser.add_argument("--include-d11", action="store_true", default=True, help="Include Depth-11 Stockfish archives for deeper endgame/positional play (default: True)")
    parser.add_argument("--force-rebuild", action="store_true", default=False, help="Force re-downloading and re-unpacking binpacks even if existing ones are present")
    parser.add_argument("--augment-flip", action="store_true", default=True, help="Apply random horizontal board reflection during training (default: True)")
    parser.add_argument("--external-data", type=str, default=None, help="Additional external .binpack file, directory, or glob pattern to include")
    parser.add_argument("--data-dir", type=str, default=os.path.join(script_dir, "data", "raw"), help="Raw data directory")
    parser.add_argument("--preprocessed-dir", type=str, default=os.path.join(script_dir, "data", "preprocessed"), help="Binary pack directory")
    parser.add_argument("--checkpoints-dir", type=str, default=os.path.join(script_dir, "checkpoints"), help="Checkpoints directory")
    parser.add_argument("--deploy-dir", type=str, default=default_networks, help="Target .nnue deploy directory (default: networks/)")
    parser.add_argument("--skip-download", action="store_true", default=False, help="Skip downloading if raw files exist")
    parser.add_argument("--skip-preprocess", action="store_true", default=False, help="Skip preprocessing if binpacks exist")
    args = parser.parse_args()

    # Apply preset configurations if specified
    if args.preset == "quick":
        args.positions_per_model = 500_000
        args.epochs = 30
        args.d9_chunks = 25
    elif args.preset == "standard":
        args.positions_per_model = 5_000_000
        args.epochs = 60
        args.d9_chunks = 150
    elif args.preset == "large":
        args.positions_per_model = 15_000_000
        args.epochs = 80
        args.d9_chunks = 350
    elif args.preset == "massive":
        args.positions_per_model = 30_000_000
        args.epochs = 100
        args.d9_chunks = 0  # full download
        args.full_download = True

    if args.full_download:
        args.d9_chunks = 0

    selected_models = parse_models_arg(args.models)

    overall_start = time.time()

    # Automatic Hardware Detection & Profile Pinning
    if args.auto:
        hw_cfg = auto_tune_hardware(
            user_batch_size=args.batch_size,
            user_workers=args.workers,
            user_compile=args.compile,
            is_massive_preset=(args.preset == "massive"),
            verbose=True,
        )
        effective_batch_size = hw_cfg["batch_size"]
        effective_workers = hw_cfg["workers"]
        effective_prefetch = args.prefetch_batches or hw_cfg["prefetch_batches"]
        effective_chunk_size = args.chunk_size or hw_cfg["chunk_size"]
        compile_model = hw_cfg["compile_model"]
        device = hw_cfg["device"]
    else:
        device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        effective_batch_size = args.batch_size or (16384 if device.type == "cuda" else 8192)
        effective_workers = args.workers
        effective_prefetch = args.prefetch_batches or (16 if device.type == "cuda" else 4)
        effective_chunk_size = args.chunk_size or 65536
        compile_model = bool(args.compile)
        if device.type == "cpu":
            torch.set_num_threads(os.cpu_count() or 4)

    print_banner(f"ANAK 8-MODEL MULTI-EXPERT NNUE TRAINING PIPELINE (PyTorch on {device})")
    print(f"Target Models:      {', '.join(m.upper() for m in selected_models)}")
    print(f"Target Positions:   {args.positions_per_model:,} per expert model")
    print(f"Epochs & Batch:     {args.epochs} epochs | Pinned Batch Size: {effective_batch_size:,}")
    print(f"Data Augmentation:  {'Horizontal Board Reflection' if args.augment_flip else 'Disabled'}")
    print(f"Dataset Archives:   Depth-9 & {'Depth-11 Included' if args.include_d11 else 'Depth-9 Only'}")

    alt_data_dir = os.path.join(script_dir, "data")

    # Step 1 & 2: Download Stockfish Depth-9/11 datasets and convert to Anak binpacks
    ensure_stockfish_d9_binpacks(
        raw_data_dir=args.data_dir,
        preprocessed_dir=args.preprocessed_dir,
        min_records_per_pack=args.positions_per_model,
        chunks_per_file=args.d9_chunks,
        required_models=selected_models,
        force_rebuild=args.force_rebuild,
        include_d11=args.include_d11,
    )

    # Sync binpacks to training/data/ only when small (< 100 MB) to avoid duplicating multi-GB datasets
    for m in ALL_8_MODELS:
        src_bp = os.path.join(args.preprocessed_dir, f"{m}.binpack")
        dst_bp = os.path.join(alt_data_dir, f"{m}.binpack")
        if os.path.isfile(src_bp) and os.path.abspath(src_bp) != os.path.abspath(dst_bp):
            if os.path.getsize(src_bp) < 100 * 1024 * 1024:
                shutil.copy2(src_bp, dst_bp)

    os.makedirs(args.checkpoints_dir, exist_ok=True)
    os.makedirs(args.deploy_dir, exist_ok=True)

    trained_checkpoints = {}
    for m in selected_models:
        bin_path = os.path.join(args.preprocessed_dir, f"{m}.binpack")
        # Combine with external data if specified
        if args.external_data:
            resolved_ext = resolve_binpack_files(args.external_data)
            if resolved_ext:
                bin_path = [bin_path] + resolved_ext

        is_micro = is_micro_model(m)
        model_batch_size = (effective_batch_size * 2) if (is_micro and device.type == "cuda") else effective_batch_size
        model_lr = (args.lr * 2.0) if is_micro else args.lr
        model_epochs = max(10, args.epochs // 2) if is_micro else args.epochs
        model_patience = max(3, args.patience // 2) if (is_micro and args.patience > 0) else args.patience

        # Collect mix sources from all other expert binpacks that exist on disk
        other_binpacks = [os.path.join(args.preprocessed_dir, f"{other}.binpack") for other in ALL_8_MODELS if other != m]
        valid_mix = [p for p in other_binpacks if os.path.isfile(p)]

        best_pt = train_single_model(
            model_name=m,
            binpack_path=bin_path,
            epochs=model_epochs,
            batch_size=model_batch_size,
            lr=model_lr,
            device=device,
            checkpoints_dir=args.checkpoints_dir,
            warmup_epochs=args.warmup_epochs,
            val_split=args.val_split,
            augment_flip=args.augment_flip,
            lambda_wdl=args.lambda_wdl,
            patience=model_patience,
            min_delta=args.min_delta,
            mix_sources=valid_mix,
            mix_ratio=args.mix_ratio,
            compile_model=compile_model,
            workers=effective_workers,
            prefetch_batches=effective_prefetch,
            chunk_size=effective_chunk_size,
        )
        trained_checkpoints[m] = best_pt

    # Quantize and Deploy to networks/
    print_banner("QUANTIZATION & DEPLOYMENT TO ANAK ENGINE")
    print(f"Target Directory: {os.path.abspath(args.deploy_dir)}\n")

    exported_files = []
    for m, best_pt in trained_checkpoints.items():
        out_path = os.path.join(args.deploy_dir, f"{m}.nnue")
        if is_micro_model(m):
            net = MicroNNUE()
            net.load_state_dict(torch.load(best_pt, map_location="cpu"))
            export_micro(net, out_path)
        else:
            net = StandardNNUE()
            net.load_state_dict(torch.load(best_pt, map_location="cpu"))
            export_standard(net, out_path)
        exported_files.append((m, out_path))

    sync_dirs = [
        os.path.join(script_dir, "networks"),
        os.path.join(repo_root, "tools", "Anak.UI", "bin", "Release", "net11.0-windows", "networks"),
        os.path.join(repo_root, "tools", "Anak.UI", "bin", "Debug", "net11.0-windows", "networks"),
        os.path.join(repo_root, "src", "Anak.Protocol", "bin", "Release", "net11.0", "networks"),
        os.path.join(repo_root, "src", "Anak.Protocol", "bin", "Debug", "net11.0", "networks"),
        os.path.join(repo_root, "tools", "fastchess", "bin", "anak", "networks"),
    ]
    for s_dir in sync_dirs:
        if os.path.isdir(os.path.dirname(s_dir)):
            os.makedirs(s_dir, exist_ok=True)
            for m, src_f in exported_files:
                f_name = f"{m}.nnue"
                dst_f = os.path.join(s_dir, f_name)
                if os.path.isfile(src_f) and os.path.abspath(src_f) != os.path.abspath(dst_f):
                    shutil.copy2(src_f, dst_f)

    total_time = time.time() - overall_start
    print_banner("ANAK NNUE TRAINING PIPELINE COMPLETED SUCCESSFULLY!")
    print(f"Total Elapsed Time: {total_time / 60:.2f} minutes")
    print("Exported Quantized Production Networks:")
    for idx, (m, out_path) in enumerate(exported_files, 1):
        print(f"  {idx}. [{m.capitalize():10s} Model] -> {out_path} ({os.path.getsize(out_path):,} bytes)")
    print(f"\nAll {len(exported_files)} trained network(s) are in place and ready for Anak's C# engine!\n")


if __name__ == "__main__":
    main()
