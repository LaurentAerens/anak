"""
Generates or seeds baseline NNUE network binaries for all 8 Multi-Expert NNUE models:
  - StandardNNUE (768 -> 512x2 -> 64 -> 1): open, closed, endgame, queenless, imbalance, attack
  - MicroNNUE    (768 -> 16x2 -> 1):        micro, volatility
If trained checkpoints exist in training/checkpoints/, migrates any legacy (256x2 -> 32 -> 1)
checkpoints into the expanded (512x2 -> 64 -> 1) architecture while preserving learned features
and perspective-twin antisymmetry, and seeds any missing expert checkpoints.
"""

import os
import shutil
import torch
from model import StandardNNUE, MicroNNUE, clamp_nnue_weights
from export import export_standard, export_micro

STANDARD_MODELS = ["open", "closed", "endgame", "queenless", "imbalance", "attack"]
MICRO_MODELS = ["micro", "volatility"]


def load_or_migrate_standard_checkpoint(net: StandardNNUE, ckpt_path: str) -> bool:
    """
    Loads a StandardNNUE checkpoint into `net`. If the checkpoint has legacy dimensions
    (e.g. 256x2 -> 32 -> 1), embeds the learned Feature Transformer and perspective-twin
    L1/Output weights into the expanded (512x2 -> 64 -> 1) network, initializes the new
    capacity with small noise + perspective antisymmetry, and saves the upgraded checkpoint.
    Returns True if migration occurred.
    """
    state_dict = torch.load(ckpt_path, map_location="cpu")
    old_ft_w = state_dict["ft.weight"]
    old_l1_w = state_dict["l1.weight"]

    if old_ft_w.shape == net.ft.weight.shape and old_l1_w.shape == net.l1.weight.shape:
        net.load_state_dict(state_dict)
        return False

    old_h = old_ft_w.shape[0]
    old_l1 = old_l1_w.shape[0]
    old_half = old_l1 // 2

    new_h = net.hidden_dim
    new_l1 = net.l1_dim
    new_half = new_l1 // 2

    with torch.no_grad():
        # 1. Feature Transformer: copy first old_h channels, init remaining channels with small noise
        net.ft.weight.normal_(0.0, 0.02)
        net.ft.bias.fill_(0.10)
        net.ft.weight[:old_h, :].copy_(old_ft_w)
        net.ft.bias[:old_h].copy_(state_dict["ft.bias"])

        # 2. Hidden layer (L1): preserve twin structure (0..old_half-1 and new_half..new_half+old_half-1)
        net.l1.weight.normal_(0.0, 0.02)
        net.l1.bias.fill_(0.05)

        net.l1.weight[:old_half, :].zero_()
        net.l1.weight[new_half:new_half + old_half, :].zero_()

        # Map our_acc (0..old_h-1) and their_acc (new_h..new_h+old_h-1)
        net.l1.weight[:old_half, :old_h].copy_(old_l1_w[:old_half, :old_h])
        net.l1.weight[:old_half, new_h:new_h + old_h].copy_(old_l1_w[:old_half, old_h:2 * old_h])

        net.l1.weight[new_half:new_half + old_half, :old_h].copy_(old_l1_w[old_half:old_l1, :old_h])
        net.l1.weight[new_half:new_half + old_half, new_h:new_h + old_h].copy_(old_l1_w[old_half:old_l1, old_h:2 * old_h])

        net.l1.bias[:old_half].copy_(state_dict["l1.bias"][:old_half])
        net.l1.bias[new_half:new_half + old_half].copy_(state_dict["l1.bias"][old_half:old_l1])

        # 3. Output layer: copy twin weights and init new L1 output weights with small noise
        net.output.weight.normal_(0.0, 0.01)
        net.output.weight[0, :old_half].copy_(state_dict["output.weight"][0, :old_half])
        net.output.weight[0, new_half:new_half + old_half].copy_(state_dict["output.weight"][0, old_half:old_l1])
        net.output.bias.copy_(state_dict["output.bias"])

    clamp_nnue_weights(net)
    torch.save(net.state_dict(), ckpt_path)
    print(f"  [Migrated] {os.path.basename(ckpt_path)}: ({old_h}x2 -> {old_l1} -> 1) => ({new_h}x2 -> {new_l1} -> 1)")
    return True


def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)
    output_dir = os.path.join(repo_root, "networks")
    training_net_dir = os.path.join(script_dir, "networks")
    checkpoints_dir = os.path.join(script_dir, "checkpoints")

    os.makedirs(output_dir, exist_ok=True)
    os.makedirs(training_net_dir, exist_ok=True)
    os.makedirs(checkpoints_dir, exist_ok=True)

    print(f"Preparing all 8 Multi-Expert NNUE networks into: {output_dir}")
    torch.manual_seed(42)

    open_ckpt = os.path.join(checkpoints_dir, "open_best.pt")
    closed_ckpt = os.path.join(checkpoints_dir, "closed_best.pt")
    micro_ckpt = os.path.join(checkpoints_dir, "micro_best.pt")

    # First migrate open_best.pt and closed_best.pt if they exist so seeded models inherit upgraded weights
    for base_ckpt in (open_ckpt, closed_ckpt):
        if os.path.isfile(base_ckpt):
            tmp_net = StandardNNUE()
            load_or_migrate_standard_checkpoint(tmp_net, base_ckpt)

    # Seed missing checkpoints from existing trained checkpoints if available
    seed_map_standard = {
        "endgame": closed_ckpt if os.path.isfile(closed_ckpt) else open_ckpt,
        "queenless": closed_ckpt if os.path.isfile(closed_ckpt) else open_ckpt,
        "imbalance": open_ckpt if os.path.isfile(open_ckpt) else closed_ckpt,
        "attack": open_ckpt if os.path.isfile(open_ckpt) else closed_ckpt,
    }

    for m in STANDARD_MODELS:
        ckpt_path = os.path.join(checkpoints_dir, f"{m}_best.pt")
        latest_path = os.path.join(checkpoints_dir, f"{m}_latest.pt")
        net = StandardNNUE()
        if os.path.isfile(ckpt_path):
            load_or_migrate_standard_checkpoint(net, ckpt_path)
        elif m in seed_map_standard and os.path.isfile(seed_map_standard[m]):
            shutil.copy2(seed_map_standard[m], ckpt_path)
            load_or_migrate_standard_checkpoint(net, ckpt_path)
            print(f"  [Seeded] {m}_best.pt from {os.path.basename(seed_map_standard[m])}")
        else:
            clamp_nnue_weights(net)
            torch.save(net.state_dict(), ckpt_path)

        if os.path.isfile(latest_path):
            latest_net = StandardNNUE()
            load_or_migrate_standard_checkpoint(latest_net, latest_path)

        out_path = os.path.join(output_dir, f"{m}.nnue")
        export_standard(net, out_path)
        shutil.copy2(out_path, os.path.join(training_net_dir, f"{m}.nnue"))

    for m in MICRO_MODELS:
        ckpt_path = os.path.join(checkpoints_dir, f"{m}_best.pt")
        net = MicroNNUE()
        if os.path.isfile(ckpt_path):
            net.load_state_dict(torch.load(ckpt_path, map_location="cpu"))
        elif os.path.isfile(micro_ckpt):
            shutil.copy2(micro_ckpt, ckpt_path)
            net.load_state_dict(torch.load(ckpt_path, map_location="cpu"))
            print(f"  [Seeded] {m}_best.pt from micro_best.pt")
        else:
            clamp_nnue_weights(net)
            torch.save(net.state_dict(), ckpt_path)

        out_path = os.path.join(output_dir, f"{m}.nnue")
        export_micro(net, out_path)
        shutil.copy2(out_path, os.path.join(training_net_dir, f"{m}.nnue"))

    print("All 8 Multi-Expert NNUE networks generated and synced successfully!")


if __name__ == "__main__":
    main()
