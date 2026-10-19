"""
Quantization and Binary Serializer for Anak NNUE.
Exports PyTorch checkpoints (.pt) to quantized INT8/INT16 binary files (.nnue)
directly loadable by Anak's C# engine with zero-GC allocations.
"""

import argparse
import os
import struct
import torch
import numpy as np

from model import StandardNNUE, MicroNNUE, FEATURE_COUNT, STANDARD_HIDDEN_DIM, MICRO_HIDDEN_DIM

MAGIC_HEADER = 0x4B414E41  # "ANAK"
FORMAT_VERSION = 1
ARCH_STANDARD = 0x0001
ARCH_MICRO = 0x0002

WEIGHT_SCALE = 64
BIAS_SCALE = 64


def quantize_sbyte(tensor: torch.Tensor, scale: float = WEIGHT_SCALE) -> np.ndarray:
    """Multiplies by scale and clamps to int8 range [-128, 127]."""
    arr = (tensor.detach().cpu().numpy() * scale).round()
    return np.clip(arr, -128, 127).astype(np.int8)


def quantize_short(tensor: torch.Tensor, scale: float = BIAS_SCALE) -> np.ndarray:
    """Multiplies by scale and clamps to int16 range [-32768, 32767]."""
    arr = (tensor.detach().cpu().numpy() * scale).round()
    return np.clip(arr, -32768, 32767).astype(np.int16)


def export_standard(model: StandardNNUE, output_path: str):
    """Exports StandardNNUE (768 -> 256x2 -> 32 -> 1) to binary."""
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)

    # 1. Feature Transformer: PyTorch ft.weight is (256, 768) -> transpose to (768, 256)
    ft_weights = quantize_sbyte(model.ft.weight.T)  # (768, 256) int8
    ft_biases = quantize_short(model.ft.bias)       # (256,) int16

    # 2. Hidden Layer: PyTorch l1.weight is (32, 512) -> transpose to (512, 32)
    hidden_weights = quantize_sbyte(model.l1.weight.T)  # (512, 32) int8
    hidden_biases = quantize_short(model.l1.bias)       # (32,) int16

    # 3. Output Layer: PyTorch output.weight is (1, 32)
    output_weights = quantize_short(model.output.weight.squeeze(0))  # (32,) int16
    output_bias_val = int(round(model.output.bias.item() * (WEIGHT_SCALE * BIAS_SCALE)))

    with open(output_path, "wb") as f:
        # Header: Magic (4B), Version (4B), ArchId (2B), Reserved (6B)
        header = struct.pack("<IIH6s", MAGIC_HEADER, FORMAT_VERSION, ARCH_STANDARD, b"\x00" * 6)
        f.write(header)

        # Write payload
        f.write(ft_weights.tobytes())
        f.write(ft_biases.tobytes())
        f.write(hidden_weights.tobytes())
        f.write(hidden_biases.tobytes())
        f.write(output_weights.tobytes())
        f.write(struct.pack("<i", output_bias_val))

    print(f"Successfully exported Standard NNUE to: {output_path} ({os.path.getsize(output_path)} bytes)")


def export_micro(model: MicroNNUE, output_path: str):
    """Exports MicroNNUE (768 -> 16x2 -> 1) to binary."""
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)

    # 1. Feature Transformer: PyTorch ft.weight is (16, 768) -> transpose to (768, 16)
    ft_weights = quantize_sbyte(model.ft.weight.T)  # (768, 16) int8
    ft_biases = quantize_short(model.ft.bias)       # (16,) int16

    # 2. Direct Output: PyTorch output.weight is (1, 32)
    output_weights = quantize_short(model.output.weight.squeeze(0))  # (32,) int16
    output_bias_val = int(round(model.output.bias.item() * (WEIGHT_SCALE * BIAS_SCALE)))

    with open(output_path, "wb") as f:
        # Header: Magic (4B), Version (4B), ArchId (2B), Reserved (6B)
        header = struct.pack("<IIH6s", MAGIC_HEADER, FORMAT_VERSION, ARCH_MICRO, b"\x00" * 6)
        f.write(header)

        # Write payload
        f.write(ft_weights.tobytes())
        f.write(ft_biases.tobytes())
        f.write(output_weights.tobytes())
        f.write(struct.pack("<i", output_bias_val))

    print(f"Successfully exported Micro NNUE to: {output_path} ({os.path.getsize(output_path)} bytes)")


def main():
    parser = argparse.ArgumentParser(description="Export PyTorch NNUE checkpoint to quantized binary .nnue")
    parser.add_argument("--checkpoint", type=str, required=True, help="Path to input PyTorch checkpoint (.pt)")
    parser.add_argument("--model", type=str, choices=["open", "closed", "micro"], required=True,
                        help="Model architecture type")
    parser.add_argument("--output", type=str, default=None, help="Output .nnue binary file path")
    args = parser.parse_args()

    output_file = args.output or f"./networks/{args.model}.nnue"

    if args.model == "micro":
        net = MicroNNUE()
        net.load_state_dict(torch.load(args.checkpoint, map_location="cpu"))
        net.eval()
        export_micro(net, output_file)
    else:
        net = StandardNNUE()
        net.load_state_dict(torch.load(args.checkpoint, map_location="cpu"))
        net.eval()
        export_standard(net, output_file)


if __name__ == "__main__":
    main()
