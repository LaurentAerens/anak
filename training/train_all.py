"""
All-in-One Master Training Pipeline for Anak NNUE:
1. Gathers massive multi-source evaluated chess data: TCEC, Stockfish, Leela, Lichess, Generation
2. Executes one-time parallel preprocessing & pre-splitting into open, closed, and micro binpacks
3. Trains enhanced Open-NNUE, Closed-NNUE, and Micro-NNUE with ModelEMA, ModernNNUELoss, and >1M pos/sec throughput
4. Quantizes and deploys all 3 models to networks/ for immediate zero-jitter use in Anak's C# engine.
"""

import argparse
import os
import sys
import time
from typing import List, Optional
import torch

from model import StandardNNUE, MicroNNUE, ModelEMA, clamp_nnue_weights
from loss import ModernNNUELoss, BlunderLoss
from fast_loader import FastMemoryMappedBatchLoader
from binpack import BinpackReader
from preprocess import preprocess_data
from export import export_standard, export_micro
from dataset import find_pgn_files
from download_data import download_all_sources
from tqdm import tqdm


def print_banner(text: str):
    print("\n" + "=" * 75)
    print(f"  {text}")
    print("=" * 75)


def gather_massive_data(raw_data_dir: str) -> List[str]:
    """
    Ensures training/data/raw/ contains a rich, diverse multi-source corpus.
    Downloads TCEC, Stockfish, Leela, and Lichess databases.
    """
    print_banner("STEP 1: MULTI-SOURCE DATA GATHERING (TCEC + STOCKFISH + LEELA + LICHESS)")
    os.makedirs(raw_data_dir, exist_ok=True)

    existing_files = find_pgn_files(raw_data_dir)
    if existing_files and len(existing_files) >= 2:
        print(f"Found {len(existing_files)} existing data archive(s) in {raw_data_dir}:")
        for f in existing_files[:4]:
            print(f"  - {os.path.basename(f)} ({os.path.getsize(f):,} bytes)")
        if len(existing_files) > 4:
            print(f"  ... and {len(existing_files) - 4} more files.")
        return existing_files

    print("Fetching complete multi-source chess corpus from public archives...")
    download_all_sources(raw_data_dir)

    files = find_pgn_files(raw_data_dir)
    if not files:
        # Fallback to local position generation if machine is offline
        print("\nNotice: Offline or network unreachable. Falling back to high-volume local generation...")
        from generate_data import find_stockfish_binary, load_seed_openings, worker_task
        sf_bin = find_stockfish_binary()
        if sf_bin:
            workers = max(1, (os.cpu_count() or 4) - 2)
            openings = load_seed_openings()
            from multiprocessing import Pool
            for mode in ["open", "closed", "blunders"]:
                out_pgn = os.path.join(raw_data_dir, f"gen_{mode}.pgn")
                tasks = [(i, sf_bin, mode, 300 // workers, 8, openings, None, False, None) for i in range(workers)]
                with Pool(processes=workers) as pool:
                    res = pool.map(worker_task, tasks)
                with open(out_pgn, "w", encoding="utf-8") as f:
                    for r in res:
                        for g in r:
                            f.write(g + "\n\n")

    return find_pgn_files(raw_data_dir)


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
    Trains one NNUE model on its preprocessed binpack using ModelEMA and ModernNNUELoss.
    Returns path to best checkpoint (.pt).
    """
    print_banner(f"TRAINING ENHANCED {model_name.upper()}-NNUE ({epochs} Epochs on {device})")
    print(f"Source Binpack: {binpack_path} ({os.path.getsize(binpack_path):,} bytes)")

    if model_name in ("micro", "small"):
        model = MicroNNUE().to(device)
        criterion = BlunderLoss()
    else:
        model = StandardNNUE().to(device)
        criterion = ModernNNUELoss(lambda_wdl=0.40, eval_scale=2.50)

    # Initialize Model Exponential Moving Average (Polyak Averaging)
    ema = ModelEMA(model, decay=0.999)

    optimizer = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=1e-5)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=epochs)

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
            batch_size=batch_size,
            shuffle=True,
            device=device
        )

        pbar = tqdm(loader, desc=f"{model_name.capitalize()} Epoch {epoch}/{epochs}")
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
            optimizer.step()

            # Quantization-aware clamping to prevent INT8 overflow
            clamp_nnue_weights(model, max_val=1.98)

            # Update EMA shadow weights
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
            # Save EMA weights into checkpoint
            backup = ema.store_and_apply(model)
            torch.save(model.state_dict(), best_path)
            ema.restore(model, backup)

    print(f"--> Finished {model_name.upper()}! Best Loss: {best_loss:.4f} -> {best_path}")
    return best_path


def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)
    default_networks = os.path.join(repo_root, "networks")

    parser = argparse.ArgumentParser(description="All-in-One Anak NNUE Pipeline: Gather, Preprocess, Train, Deploy")
    parser.add_argument("--epochs", type=int, default=30, help="Number of epochs per model (default: 30)")
    parser.add_argument("--batch-size", type=int, default=8192, help="Batch size (default: 8192)")
    parser.add_argument("--lr", type=float, default=1e-3, help="Learning rate (default: 1e-3)")
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

    # Step 1: Data Gathering (TCEC + Stockfish + Leela + Lichess)
    if not args.skip_download:
        raw_files = gather_massive_data(args.data_dir)
    else:
        raw_files = find_pgn_files(args.data_dir)
        print(f"Skipping download. Found {len(raw_files)} existing data file(s).")

    if not raw_files:
        print("ERROR: No raw training data found to preprocess!")
        sys.exit(1)

    # Step 2: One-Time Preprocessing & Pre-Splitting
    open_bin = os.path.join(args.preprocessed_dir, "open.binpack")
    closed_bin = os.path.join(args.preprocessed_dir, "closed.binpack")
    micro_bin = os.path.join(args.preprocessed_dir, "micro.binpack")

    binpacks_exist = all(os.path.isfile(p) for p in [open_bin, closed_bin, micro_bin])
    if not args.skip_preprocess or not binpacks_exist:
        print_banner("STEP 2: ONE-TIME PARALLEL PREPROCESSING & PRE-SPLITTING")
        preprocess_data(
            input_paths=[args.data_dir],
            output_dir=args.preprocessed_dir
        )
    else:
        print_banner("STEP 2: PREPROCESSING (CACHED)")
        print("Found existing preprocessed binpacks. Skipping re-processing.")

    os.makedirs(args.checkpoints_dir, exist_ok=True)
    os.makedirs(args.deploy_dir, exist_ok=True)

    # Step 3: Train Enhanced Open-NNUE
    best_open_pt = train_single_model(
        model_name="open",
        binpack_path=open_bin,
        epochs=args.epochs,
        batch_size=args.batch_size,
        lr=args.lr,
        device=device,
        checkpoints_dir=args.checkpoints_dir
    )

    # Step 4: Train Enhanced Closed-NNUE
    best_closed_pt = train_single_model(
        model_name="closed",
        binpack_path=closed_bin,
        epochs=args.epochs,
        batch_size=args.batch_size,
        lr=args.lr,
        device=device,
        checkpoints_dir=args.checkpoints_dir
    )

    # Step 5: Train Enhanced Micro-NNUE
    best_micro_pt = train_single_model(
        model_name="micro",
        binpack_path=micro_bin,
        epochs=args.epochs,
        batch_size=args.batch_size,
        lr=args.lr * 2.0,
        device=device,
        checkpoints_dir=args.checkpoints_dir
    )

    # Step 6: Quantize and Deploy to networks/
    print_banner("STEP 6: QUANTIZATION & DEPLOYMENT TO ANAK ENGINE")
    print(f"Target Directory: {os.path.abspath(args.deploy_dir)}\n")

    # Deploy Open
    open_model = StandardNNUE()
    open_model.load_state_dict(torch.load(best_open_pt, map_location="cpu"))
    open_out = os.path.join(args.deploy_dir, "open.nnue")
    export_standard(open_model, open_out)

    # Deploy Closed
    closed_model = StandardNNUE()
    closed_model.load_state_dict(torch.load(best_closed_pt, map_location="cpu"))
    closed_out = os.path.join(args.deploy_dir, "closed.nnue")
    export_standard(closed_model, closed_out)

    # Deploy Micro
    micro_model = MicroNNUE()
    micro_model.load_state_dict(torch.load(best_micro_pt, map_location="cpu"))
    micro_out = os.path.join(args.deploy_dir, "micro.nnue")
    export_micro(micro_model, micro_out)

    # Sync to build output and training directories
    import shutil
    sync_dirs = [
        os.path.join(script_dir, "networks"),
        os.path.join(repo_root, "tools", "Anak.UI", "bin", "Release", "net11.0-windows", "networks"),
        os.path.join(repo_root, "tools", "Anak.UI", "bin", "Debug", "net11.0-windows", "networks"),
        os.path.join(repo_root, "src", "Anak.Protocol", "bin", "Release", "net11.0", "networks"),
        os.path.join(repo_root, "src", "Anak.Protocol", "bin", "Debug", "net11.0", "networks"),
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
