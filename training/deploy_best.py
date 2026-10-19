"""
Anak NNUE One-Click Deployment Script.
Finds the best trained checkpoints in ./checkpoints/, quantizes them to INT8/INT16,
and deploys them directly into networks/ for immediate zero-GC loading by the C# engine.
"""

import argparse
import os
import shutil
import time
import torch

from model import StandardNNUE, MicroNNUE
from export import export_standard, export_micro


def find_repo_root():
    """Locates the repository root by checking for Anak.sln."""
    current = os.path.dirname(os.path.abspath(__file__))
    for _ in range(5):
        if os.path.exists(os.path.join(current, "Anak.sln")):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            break
        current = parent
    return os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))


def find_checkpoint_dirs(specified_dir=None):
    """Locates all potential directories containing trained checkpoints."""
    candidates = []
    if specified_dir:
        candidates.append(os.path.abspath(specified_dir))

    cwd = os.getcwd()
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = find_repo_root()
    candidates.extend([
        os.path.join(cwd, "checkpoints"),
        os.path.join(cwd, "training", "checkpoints"),
        os.path.join(script_dir, "checkpoints"),
        os.path.join(repo_root, "training", "checkpoints"),
        os.path.join(repo_root, "checkpoints"),
    ])

    valid = []
    for c in candidates:
        if os.path.isdir(c) and c not in valid:
            valid.append(c)
    return valid


def deploy_model(model_type: str, checkpoint_path: str, output_path: str):
    """Loads checkpoint, quantizes, and writes .nnue binary."""
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)

    if model_type in ("micro", "small"):
        net = MicroNNUE()
        net.load_state_dict(torch.load(checkpoint_path, map_location="cpu"))
        net.eval()
        export_micro(net, output_path)
    else:
        net = StandardNNUE()
        net.load_state_dict(torch.load(checkpoint_path, map_location="cpu"))
        net.eval()
        export_standard(net, output_path)


def main():
    parser = argparse.ArgumentParser(description="Deploy best trained checkpoints to Anak's networks/ folder")
    parser.add_argument("--checkpoints", type=str, default=None, help="Checkpoints directory")
    parser.add_argument("--output", type=str, default=None, help="Target networks directory")
    parser.add_argument("--model", type=str, choices=["all", "open", "closed", "micro", "small"], default="all",
                        help="Model to deploy (default: all)")
    args = parser.parse_args()

    repo_root = find_repo_root()
    checkpoint_dirs = find_checkpoint_dirs(args.checkpoints)
    networks_dir = args.output or os.path.join(repo_root, "networks")
    os.makedirs(networks_dir, exist_ok=True)

    # Secondary target directories (e.g. build output if exists)
    secondary_dirs = [
        os.path.join(repo_root, "src", "Anak.Protocol", "bin", "Release", "net11.0", "networks"),
        os.path.join(repo_root, "src", "Anak.Protocol", "bin", "Debug", "net11.0", "networks"),
        os.path.join(repo_root, "tools", "Anak.UI", "bin", "Release", "net11.0-windows", "networks"),
        os.path.join(repo_root, "tools", "Anak.UI", "bin", "Debug", "net11.0-windows", "networks"),
        os.path.join(repo_root, "tools", "Anak.UI", "bin", "Release", "net11.0", "networks"),
        os.path.join(repo_root, "tools", "Anak.UI", "bin", "Debug", "net11.0", "networks"),
    ]

    print("\n" + "=" * 70)
    print("   ANAK MULTI-TIER NNUE MODEL DEPLOYMENT")
    print("=" * 70)
    print(f"Checkpoint folders searched: {', '.join(checkpoint_dirs) if checkpoint_dirs else 'None found'}")
    print(f"Networks target:             {os.path.abspath(networks_dir)}")
    print("-" * 70)

    models_to_check = [
        ("open", ["open_best.pt", "open_latest.pt"], "open.nnue", "Standard Open-NNUE"),
        ("closed", ["closed_best.pt", "closed_latest.pt"], "closed.nnue", "Standard Closed-NNUE"),
        ("micro", ["small_best.pt", "micro_best.pt", "small_latest.pt", "micro_latest.pt"], "micro.nnue", "Small/Micro Tactical Watchdog"),
    ]

    deployed_count = 0

    for m_type, candidate_names, out_filename, display_name in models_to_check:
        if args.model != "all":
            selected = "micro" if args.model == "small" else args.model
            if m_type != selected:
                continue

        # Find best and newest available checkpoint across searched directories
        found_ckpt = None
        newest_mtime = -1
        for c_dir in checkpoint_dirs:
            for name in candidate_names:
                p = os.path.join(c_dir, name)
                if os.path.isfile(p):
                    mtime = os.path.getmtime(p)
                    if mtime > newest_mtime:
                        newest_mtime = mtime
                        found_ckpt = p

        target_file = os.path.join(networks_dir, out_filename)

        if found_ckpt:
            size_mb = os.path.getsize(found_ckpt) / (1024 * 1024)
            mtime = time.strftime("%Y-%m-%d %H:%M", time.localtime(os.path.getmtime(found_ckpt)))
            print(f"\n[OK] Deploying {display_name}:")
            print(f"     Source: {os.path.basename(found_ckpt)} ({size_mb:.2f} MB, modified {mtime})")

            deploy_model(m_type, found_ckpt, target_file)
            deployed_count += 1

            # Sync to build directories if present
            for s_dir in secondary_dirs:
                if os.path.isdir(os.path.dirname(s_dir)):
                    os.makedirs(s_dir, exist_ok=True)
                    shutil.copy2(target_file, os.path.join(s_dir, out_filename))
        else:
            print(f"\n[--] {display_name}:")
            print(f"     No checkpoint found in {checkpoints_dir}")
            print(f"     -> Train using: python train.py --model {m_type}")

    print("\n" + "=" * 70)
    if deployed_count > 0:
        print(f"Successfully deployed {deployed_count} model(s) to: {os.path.abspath(networks_dir)}")
        print("Anak's C# engine will automatically load these weights upon launch!")
    else:
        print("No checkpoints were deployed. Train a model first using 'python train.py'.")
    print("=" * 70 + "\n")


if __name__ == "__main__":
    main()
