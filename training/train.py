"""
Training script for Anak NNUE models on TCEC datasets.
Supports training Open, Closed, and Micro networks with Cosine Annealing and early checkpointing.
"""

import argparse
import os
import sys
from typing import List, Optional
import torch
from torch.utils.data import DataLoader
from tqdm import tqdm

from model import StandardNNUE, MicroNNUE
from dataset import TcecDataset, find_pgn_files
from loss import TcecBlendedLoss, BlunderLoss


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

    # Filter by source tags (e.g. 'generated', 'tcec', 'lichess', or comma-separated combo)
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
            elif s == "stockfish" and "stockfish" in basename:
                matched = True
            elif s in ("leela", "lc0") and ("leela" in basename or "lc0" in basename):
                matched = True
            elif s in basename:
                matched = True
        if matched:
            filtered.append(f)

    return filtered


def parse_args():
    parser = argparse.ArgumentParser(description="Train Anak NNUE models on TCEC, Stockfish, Leela, Lichess, or Generated datasets")
    parser.add_argument("--model", type=str, choices=["open", "closed", "micro", "small"], default="open",
                        help="Target network to train (open, closed, or micro/small)")
    parser.add_argument("--source", type=str, default="all",
                        help="Data source filter (tcec, generated, lichess, stockfish, leela, all, or comma-separated combo like 'tcec,generated')")
    parser.add_argument("--data", type=str, default=None,
                        help="Path to PGN file or data directory (defaults to auto-discovering ./data or training/data)")
    parser.add_argument("--epochs", type=int, default=40, help="Number of training epochs")
    parser.add_argument("--batch-size", type=int, default=4096, help="Batch size")
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

    # 1. Resolve Data Files
    data_files = resolve_data_files(args.data, source=args.source)
    if not data_files:
        print("\n" + "=" * 70)
        print("ERROR: No matching PGN training data files found!")
        if args.source != "all":
            print(f"Filter requested source: '{args.source}'")
        if args.data:
            print(f"Specified target path: '{args.data}'")
        print("\nSearched standard locations:")
        print(f"  - {os.path.abspath('data')}")
        print(f"  - {os.path.abspath(os.path.join(os.path.dirname(__file__), 'data'))}")
        print("\nTo automatically download TCEC game archives, run:")
        print("  python download_tcec.py --season 25 --division all --output ./data")
        print("\nOr manually provide a PGN file or directory via:")
        print("  python train.py --data <path/to/archive.pgn>")
        print("=" * 70 + "\n")
        sys.exit(1)

    os.makedirs(args.output, exist_ok=True)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"=== Training Anak NNUE: {args.model.upper()} Model on {device} ===")
    print(f"Data Source Filter: {args.source.upper()} ({len(data_files)} matching files)")
    print(f"Found PGN file(s):")
    for f in data_files[:5]:
        print(f"  - {f} ({os.path.getsize(f):,} bytes)")
    if len(data_files) > 5:
        print(f"  ... and {len(data_files) - 5} more files")

    # 2. Instantiate Model & Loss
    if args.model in ("micro", "small"):
        model = MicroNNUE().to(device)
        criterion = BlunderLoss()
    else:
        model = StandardNNUE().to(device)
        criterion = TcecBlendedLoss(lambda_wdl=args.lambda_wdl)

    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-5)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=args.epochs)

    best_loss = float("inf")

    # 3. Training Loop
    for epoch in range(1, args.epochs + 1):
        dataset = TcecDataset(
            pgn_path=data_files,
            target_model=args.model,
            max_samples=args.max_samples
        )
        dataloader = DataLoader(dataset, batch_size=args.batch_size, num_workers=0)

        model.train()
        total_loss = 0.0
        batch_count = 0

        pbar = tqdm(dataloader, desc=f"Epoch {epoch}/{args.epochs}")
        for batch in pbar:
            our_feat = batch["our_features"].to(device)
            their_feat = batch["their_features"].to(device)
            target_wdl = batch["target_wdl"].to(device)
            target_eval = batch["target_eval"].to(device)

            optimizer.zero_grad()
            preds = model(our_feat, their_feat)

            if args.model in ("micro", "small"):
                loss = criterion(preds, target_eval)
            else:
                loss = criterion(preds, target_wdl, target_eval)

            loss.backward()
            optimizer.step()

            total_loss += loss.item()
            batch_count += 1
            pbar.set_postfix({"loss": f"{loss.item():.4f}"})

        scheduler.step()
        avg_loss = total_loss / max(1, batch_count)
        print(f"Epoch {epoch} Completed | Average Loss: {avg_loss:.4f} | LR: {scheduler.get_last_lr()[0]:.6f}")

        # Save checkpoint (save both micro and small names if small/micro)
        names = ["micro", "small"] if args.model in ("micro", "small") else [args.model]
        for name in names:
            checkpoint_path = os.path.join(args.output, f"{name}_latest.pt")
            torch.save(model.state_dict(), checkpoint_path)

        if avg_loss < best_loss:
            best_loss = avg_loss
            for name in names:
                best_path = os.path.join(args.output, f"{name}_best.pt")
                torch.save(model.state_dict(), best_path)
            print(f"--> Saved new best checkpoint: {os.path.join(args.output, f'{args.model}_best.pt')}")

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
