"""
Generates synthetic baseline NNUE network binaries for testing and local development.
Creates networks/open.nnue, networks/closed.nnue, and networks/micro.nnue.
"""

import os
import torch
from model import StandardNNUE, MicroNNUE
from export import export_standard, export_micro


def main():
    output_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "networks"))
    os.makedirs(output_dir, exist_ok=True)

    print(f"Generating synthetic baseline NNUE networks into: {output_dir}")

    torch.manual_seed(42)

    # 1. Open Model
    open_net = StandardNNUE()
    # Initialize with small weights
    for p in open_net.parameters():
        p.data.normal_(0.0, 0.02)
    export_standard(open_net, os.path.join(output_dir, "open.nnue"))

    # 2. Closed Model
    closed_net = StandardNNUE()
    for p in closed_net.parameters():
        p.data.normal_(0.0, 0.02)
    export_standard(closed_net, os.path.join(output_dir, "closed.nnue"))

    # 3. Micro Model
    micro_net = MicroNNUE()
    for p in micro_net.parameters():
        p.data.normal_(0.0, 0.02)
    export_micro(micro_net, os.path.join(output_dir, "micro.nnue"))

    print("Synthetic baseline networks generated successfully!")


if __name__ == "__main__":
    main()
