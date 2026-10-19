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
from typing import List
import chess
import numpy as np
import torch
from tqdm import tqdm

from model import StandardNNUE, MicroNNUE, ModelEMA, clamp_nnue_weights
from loss import ModernNNUELoss, BlunderLoss
from fast_loader import FastMemoryMappedBatchLoader
from binpack import BinpackReader, BinpackWriter
from preprocess import preprocess_data
from export import export_standard, export_micro
from dataset import find_pgn_files, encode_board_features, calculate_openness
from download_data import download_stockfish_d9_binpacks, convert_stockfish_d9_to_anak
from generate_data import BuiltinTacticalEvaluator, load_seed_openings


def print_banner(text: str):
    print("\n" + "=" * 75)
    print(f"  {text}")
    print("=" * 75)


def ensure_stockfish_d9_binpacks(
    raw_data_dir: str,
    preprocessed_dir: str,
    min_records_per_pack: int = 300_000,
    chunks_per_file: int = 12
):
    """
    Ensures open.binpack, closed.binpack, and micro.binpack in preprocessed_dir contain
    at least min_records_per_pack positions from the Stockfish 500M Depth-9 database.
    """
    os.makedirs(preprocessed_dir, exist_ok=True)
    open_path = os.path.join(preprocessed_dir, "open.binpack")
    closed_path = os.path.join(preprocessed_dir, "closed.binpack")
    micro_path = os.path.join(preprocessed_dir, "micro.binpack")

    needs_build = False
    for path in (open_path, closed_path, micro_path):
        if not os.path.isfile(path):
            needs_build = True
            break
        try:
            r = BinpackReader(path)
            count = len(r)
            r.close()
            if count < min_records_per_pack:
                needs_build = True
                break
        except Exception:
            needs_build = True
            break

    if not needs_build:
        print_banner("STEP 1 & 2: STOCKFISH DEPTH-9 BINPACKS READY")
        for name, path in [("Open", open_path), ("Closed", closed_path), ("Micro", micro_path)]:
            r = BinpackReader(path)
            print(f"  - {name:6s} Binpack: {len(r):,} positions ({os.path.getsize(path):,} bytes)")
            r.close()
        return

    print_banner("STEP 1: DOWNLOADING STOCKFISH 500M DEPTH-9 (d9) TRAINING DATABASE")
    # Ensure we download enough 200k-position chunks to satisfy min_records_per_pack
    required_chunks = max(chunks_per_file, math.ceil((min_records_per_pack * 3.5) / 200_000))
    sf_files = download_stockfish_d9_binpacks(raw_data_dir, chunks_per_file=required_chunks)

    print_banner("STEP 2: NATIVE C++20 UNPACKING & GLOBAL SHUFFLING INTO ANAK BINPACKS")
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
    binpack_path: str,
    epochs: int,
    batch_size: int,
    lr: float,
    device: torch.device,
    checkpoints_dir: str
) -> str:
    """
    Trains one NNUE model on its preprocessed Stockfish d9 binpack using 100% trainable weights,
    step-aware ModelEMA, and ModernNNUELoss / BlunderLoss.
    Returns path to best checkpoint (.pt).
    """
    print_banner(f"TRAINING {model_name.upper()}-NNUE ({epochs} Epochs on {device})")
    reader = BinpackReader(binpack_path)
    total_records = len(reader)
    reader.close()

    effective_batch_size = min(batch_size, max(256, total_records // 32))
    print(f"Source Binpack: {binpack_path} ({total_records:,} positions, {os.path.getsize(binpack_path):,} bytes)")
    print(f"Batch Size:     {effective_batch_size:,} ({math.ceil(total_records / effective_batch_size):,} steps/epoch)")

    if model_name in ("micro", "small"):
        model = MicroNNUE().to(device)
        criterion = BlunderLoss()
    else:
        model = StandardNNUE().to(device)
        criterion = ModernNNUELoss(lambda_wdl=0.25, eval_scale=2.50)

    ema = ModelEMA(model, decay=0.995)

    optimizer = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=1e-4)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=epochs, eta_min=lr * 0.05)

    best_loss = float("inf")
    best_path = os.path.join(checkpoints_dir, f"{model_name}_best.pt")

    for epoch in range(1, epochs + 1):
        epoch_start = time.time()
        model.train()
        total_loss = 0.0
        batch_count = 0
        total_positions = 0

        loader = FastMemoryMappedBatchLoader(
            binpack_path,
            batch_size=effective_batch_size,
            shuffle=True,
            device=device
        )

        pbar = tqdm(loader, desc=f"{model_name.capitalize()} Epoch {epoch}/{epochs}", leave=False)
        for batch in pbar:
            our_feat = batch["our_features"]
            their_feat = batch["their_features"]
            target_wdl = batch["target_wdl"]
            target_eval = batch["target_eval"]

            optimizer.zero_grad()
            preds = model(our_feat, their_feat)

            if model_name in ("micro", "small"):
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
        loader.close()

        epoch_time = time.time() - epoch_start
        throughput = total_positions / max(0.001, epoch_time)
        avg_loss = total_loss / max(1, batch_count)

        print(f"  Epoch {epoch:2d}/{epochs} | Loss: {avg_loss:.4f} | Throughput: {throughput:,.0f} pos/sec ({epoch_time:.2f}s) | LR: {scheduler.get_last_lr()[0]:.6f}")

        if avg_loss < best_loss:
            best_loss = avg_loss
            backup = ema.store_and_apply(model)
            torch.save(model.state_dict(), best_path)
            ema.restore(model, backup)

    model.load_state_dict(torch.load(best_path, map_location=device))
    clamp_nnue_weights(model)
    torch.save(model.state_dict(), best_path)
    verify_model_sanity(model, model_name)

    print(f"--> Finished {model_name.upper()}! Best Loss: {best_loss:.4f} -> {best_path}")
    return best_path


def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)
    default_networks = os.path.join(repo_root, "networks")

    parser = argparse.ArgumentParser(description="All-in-One Anak NNUE Pipeline: Stockfish d9 Gather, Unpack, Train, Deploy")
    parser.add_argument("--epochs", type=int, default=100, help="Number of epochs per model (default: 100)")
    parser.add_argument("--batch-size", type=int, default=8192, help="Batch size (default: 8192)")
    parser.add_argument("--lr", type=float, default=1e-3, help="Learning rate (default: 1e-3)")
    parser.add_argument("--positions-per-model", type=int, default=1_000_000, help="Target Stockfish d9 positions per expert binpack (default: 1,000,000 = 3,000,000 total)")
    parser.add_argument("--d9-chunks", type=int, default=18, help="Number of 200k-position BINP chunks per d9 file (default: 18 = 7.2M raw positions)")
    parser.add_argument("--data-dir", type=str, default=os.path.join(script_dir, "data", "raw"), help="Raw data directory")
    parser.add_argument("--preprocessed-dir", type=str, default=os.path.join(script_dir, "data", "preprocessed"), help="Binary pack directory")
    parser.add_argument("--checkpoints-dir", type=str, default=os.path.join(script_dir, "checkpoints"), help="Checkpoints directory")
    parser.add_argument("--deploy-dir", type=str, default=default_networks, help="Target .nnue deploy directory (default: networks/)")
    parser.add_argument("--skip-download", action="store_true", default=False, help="Skip downloading if raw files exist")
    parser.add_argument("--skip-preprocess", action="store_true", default=False, help="Skip preprocessing if binpacks exist")
    args = parser.parse_args()

    overall_start = time.time()
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print_banner(f"ANAK NNUE AUTOMATED MASTER TRAINING PIPELINE (PyTorch on {device})")

    open_bin = os.path.join(args.preprocessed_dir, "open.binpack")
    closed_bin = os.path.join(args.preprocessed_dir, "closed.binpack")
    micro_bin = os.path.join(args.preprocessed_dir, "micro.binpack")
    alt_data_dir = os.path.join(script_dir, "data")

    # Step 1 & 2: Download Stockfish 500M Depth-9 dataset and convert to Anak binpacks
    ensure_stockfish_d9_binpacks(
        raw_data_dir=args.data_dir,
        preprocessed_dir=args.preprocessed_dir,
        min_records_per_pack=args.positions_per_model,
        chunks_per_file=args.d9_chunks
    )

    # Sync binpacks to training/data/ for tools that look there
    for m in ["open", "closed", "micro"]:
        src_bp = os.path.join(args.preprocessed_dir, f"{m}.binpack")
        dst_bp = os.path.join(alt_data_dir, f"{m}.binpack")
        if os.path.isfile(src_bp) and os.path.abspath(src_bp) != os.path.abspath(dst_bp):
            shutil.copy2(src_bp, dst_bp)

    os.makedirs(args.checkpoints_dir, exist_ok=True)
    os.makedirs(args.deploy_dir, exist_ok=True)

    # Step 3: Train Open-NNUE
    best_open_pt = train_single_model(
        model_name="open",
        binpack_path=open_bin,
        epochs=args.epochs,
        batch_size=args.batch_size,
        lr=args.lr,
        device=device,
        checkpoints_dir=args.checkpoints_dir
    )

    # Step 4: Train Closed-NNUE
    best_closed_pt = train_single_model(
        model_name="closed",
        binpack_path=closed_bin,
        epochs=args.epochs,
        batch_size=args.batch_size,
        lr=args.lr,
        device=device,
        checkpoints_dir=args.checkpoints_dir
    )

    # Step 5: Train Micro-NNUE
    best_micro_pt = train_single_model(
        model_name="micro",
        binpack_path=micro_bin,
        epochs=args.epochs,
        batch_size=args.batch_size,
        lr=args.lr * 1.5,
        device=device,
        checkpoints_dir=args.checkpoints_dir
    )

    # Step 6: Quantize and Deploy to networks/
    print_banner("STEP 6: QUANTIZATION & DEPLOYMENT TO ANAK ENGINE")
    print(f"Target Directory: {os.path.abspath(args.deploy_dir)}\n")

    open_model = StandardNNUE()
    open_model.load_state_dict(torch.load(best_open_pt, map_location="cpu"))
    open_out = os.path.join(args.deploy_dir, "open.nnue")
    export_standard(open_model, open_out)

    closed_model = StandardNNUE()
    closed_model.load_state_dict(torch.load(best_closed_pt, map_location="cpu"))
    closed_out = os.path.join(args.deploy_dir, "closed.nnue")
    export_standard(closed_model, closed_out)

    micro_model = MicroNNUE()
    micro_model.load_state_dict(torch.load(best_micro_pt, map_location="cpu"))
    micro_out = os.path.join(args.deploy_dir, "micro.nnue")
    export_micro(micro_model, micro_out)

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
            for f_name in ["open.nnue", "closed.nnue", "micro.nnue"]:
                src_f = os.path.join(args.deploy_dir, f_name)
                if os.path.isfile(src_f):
                    shutil.copy2(src_f, os.path.join(s_dir, f_name))

    total_time = time.time() - overall_start
    print_banner("ANAK NNUE TRAINING PIPELINE COMPLETED SUCCESSFULLY!")
    print(f"Total Elapsed Time: {total_time / 60:.2f} minutes")
    print("Exported Quantized Production Networks:")
    print(f"  1. [Open Model]   -> {open_out} ({os.path.getsize(open_out):,} bytes)")
    print(f"  2. [Closed Model] -> {closed_out} ({os.path.getsize(closed_out):,} bytes)")
    print(f"  3. [Micro Model]  -> {micro_out} ({os.path.getsize(micro_out):,} bytes)")
    print("\nAll 3 networks are in place and ready for Anak's C# engine!\n")


if __name__ == "__main__":
    main()
