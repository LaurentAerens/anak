"""
Training script for Anak NNUE models on TCEC datasets.
Supports training Open, Closed, and Micro networks with Cosine Annealing and early checkpointing.
"""

import argparse
import os
import torch
from torch.utils.data import DataLoader
from tqdm import tqdm

from model import StandardNNUE, MicroNNUE
from dataset import TcecDataset
from loss import TcecBlendedLoss, BlunderLoss


def parse_args():
    parser = argparse.ArgumentParser(description="Train Anak NNUE models on TCEC datasets")
    parser.add_argument("--model", type=str, choices=["open", "closed", "micro"], default="open",
                        help="Target network to train (open, closed, or micro)")
    parser.add_argument("--data", type=str, required=True,
                        help="Path to TCEC PGN or PGN.GZ file")
    parser.add_argument("--epochs", type=int, default=40, help="Number of training epochs")
    parser.add_argument("--batch-size", type=int, default=4096, help="Batch size")
    parser.add_argument("--lr", type=float, default=1e-3, help="Initial learning rate")
    parser.add_argument("--lambda-wdl", type=float, default=0.40, help="WDL loss weight factor")
    parser.add_argument("--output", type=str, default="./checkpoints", help="Output directory for checkpoints")
    parser.add_argument("--max-samples", type=int, default=None, help="Limit number of samples per epoch")
    return parser.parse_args()


def main():
    args = parse_args()
    os.makedirs(args.output, exist_ok=True)

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"=== Training Anak NNUE: {args.model.upper()} Model on {device} ===")

    # 1. Instantiate Model & Loss
    if args.model == "micro":
        model = MicroNNUE().to(device)
        criterion = BlunderLoss()
    else:
        model = StandardNNUE().to(device)
        criterion = TcecBlendedLoss(lambda_wdl=args.lambda_wdl)

    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-5)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=args.epochs)

    best_loss = float("inf")

    # 2. Training Loop
    for epoch in range(1, args.epochs + 1):
        dataset = TcecDataset(
            pgn_path=args.data,
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

            if args.model == "micro":
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

        # Save checkpoint
        checkpoint_path = os.path.join(args.output, f"{args.model}_latest.pt")
        torch.save(model.state_dict(), checkpoint_path)

        if avg_loss < best_loss:
            best_loss = avg_loss
            best_path = os.path.join(args.output, f"{args.model}_best.pt")
            torch.save(model.state_dict(), best_path)
            print(f"--> Saved new best checkpoint: {best_path}")

    print("Training finished successfully!")


if __name__ == "__main__":
    main()
