"""
Quantization and Binary Serializer for Anak NNUE.
Exports PyTorch checkpoints (.pt) to quantized INT8/INT16 binary files (.nnue)
directly loadable by Anak's C# engine with zero-GC allocations.
Includes exact C# integer inference simulation and sanity verification.
"""

import argparse
import os
import struct
import torch
import numpy as np
import chess

from model import (
    StandardNNUE,
    MicroNNUE,
    FEATURE_COUNT,
    STANDARD_HIDDEN_DIM,
    STANDARD_L1_DIM,
    MICRO_HIDDEN_DIM,
    clamp_nnue_weights,
)
from dataset import encode_board_features

MAGIC_HEADER = 0x4B414E41  # "ANAK"
FORMAT_VERSION = 1
ARCH_STANDARD = 0x0001
ARCH_MICRO = 0x0002

WEIGHT_SCALE = 64
BIAS_SCALE = 64
CLIPPED_RELU_MAX = 64
OUTPUT_SCALE = 100


def quantize_sbyte(tensor: torch.Tensor, scale: float = WEIGHT_SCALE) -> np.ndarray:
    """Multiplies by scale and clamps to int8 range [-128, 127]."""
    arr = (tensor.detach().cpu().numpy() * scale).round()
    return np.clip(arr, -128, 127).astype(np.int8)


def quantize_short(tensor: torch.Tensor, scale: float = BIAS_SCALE) -> np.ndarray:
    """Multiplies by scale and clamps to int16 range [-32768, 32767]."""
    arr = (tensor.detach().cpu().numpy() * scale).round()
    return np.clip(arr, -32768, 32767).astype(np.int16)


def simulate_csharp_standard(
    ft_weights: np.ndarray,
    ft_biases: np.ndarray,
    hidden_weights: np.ndarray,
    hidden_biases: np.ndarray,
    output_weights: np.ndarray,
    output_bias_val: int,
    our_idx: list,
    their_idx: list,
) -> int:
    """Simulates exact C# NnueInference.EvaluateStandard integer arithmetic."""
    our_acc = ft_biases.astype(np.int32).copy()
    for idx in our_idx:
        our_acc += ft_weights[idx].astype(np.int32)

    their_acc = ft_biases.astype(np.int32).copy()
    for idx in their_idx:
        their_acc += ft_weights[idx].astype(np.int32)

    our_clipped = np.clip(our_acc, 0, CLIPPED_RELU_MAX)
    their_clipped = np.clip(their_acc, 0, CLIPPED_RELU_MAX)
    activated = np.concatenate([our_clipped, their_clipped])  # (1024,)

    l1_dim = hidden_weights.shape[0]
    hidden_out = np.zeros(l1_dim, dtype=np.int32)
    for k in range(l1_dim):
        dot = int(hidden_biases[k]) * WEIGHT_SCALE + int(np.dot(activated, hidden_weights[k].astype(np.int32)))
        hidden_out[k] = np.clip(int(dot / WEIGHT_SCALE), 0, CLIPPED_RELU_MAX)

    final_sum = int(output_bias_val) + int(np.dot(hidden_out, output_weights.astype(np.int32)))
    return int((final_sum * OUTPUT_SCALE) // (WEIGHT_SCALE * WEIGHT_SCALE))


def simulate_csharp_micro(
    ft_weights: np.ndarray,
    ft_biases: np.ndarray,
    output_weights: np.ndarray,
    output_bias_val: int,
    our_idx: list,
    their_idx: list,
) -> int:
    """Simulates exact C# NnueInference.EvaluateMicro integer arithmetic."""
    our_acc = ft_biases.astype(np.int32).copy()
    for idx in our_idx:
        our_acc += ft_weights[idx].astype(np.int32)

    their_acc = ft_biases.astype(np.int32).copy()
    for idx in their_idx:
        their_acc += ft_weights[idx].astype(np.int32)

    our_clipped = np.clip(our_acc, 0, CLIPPED_RELU_MAX)
    their_clipped = np.clip(their_acc, 0, CLIPPED_RELU_MAX)
    combined = np.concatenate([our_clipped, their_clipped])  # (32,)

    final_sum = int(output_bias_val) + int(np.dot(combined, output_weights.astype(np.int32)))
    return int((final_sum * OUTPUT_SCALE) // (WEIGHT_SCALE * WEIGHT_SCALE))


def export_standard(model: StandardNNUE, output_path: str):
    """Exports StandardNNUE (768 -> 512x2 -> 64 -> 1) to binary and verifies quantization parity."""
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    clamp_nnue_weights(model)

    # 1. Feature Transformer: PyTorch ft.weight is (512, 768) -> transpose to (768, 512)
    ft_weights = quantize_sbyte(model.ft.weight.T)  # (768, 512) int8
    ft_biases = quantize_short(model.ft.bias)       # (512,) int16

    # 2. Hidden Layer: PyTorch l1.weight is (64, 1024) -> row-major (64, 1024)
    hidden_weights = quantize_sbyte(model.l1.weight)  # (64, 1024) int8
    hidden_biases = quantize_short(model.l1.bias)     # (64,) int16

    # 3. Output Layer: PyTorch output.weight is (1, 64)
    output_weights = quantize_short(model.output.weight.squeeze(0))  # (64,) int16
    output_bias_val = int(round(model.output.bias.item() * (WEIGHT_SCALE * BIAS_SCALE)))

    with open(output_path, "wb") as f:
        # Header: Magic (4B), Version (4B), ArchId (2B), HiddenDim (2B), L1Dim (2B), Reserved (2B)
        header = struct.pack(
            "<IIHHH2s",
            MAGIC_HEADER,
            FORMAT_VERSION,
            ARCH_STANDARD,
            model.hidden_dim,
            model.l1_dim,
            b"\x00\x00",
        )
        f.write(header)

        # Write payload
        f.write(ft_weights.tobytes())
        f.write(ft_biases.tobytes())
        f.write(hidden_weights.tobytes())
        f.write(hidden_biases.tobytes())
        f.write(output_weights.tobytes())
        f.write(struct.pack("<i", output_bias_val))

    # Verify quantized C# evaluation on key positions
    b_start = chess.Board(chess.STARTING_FEN)
    b_plus_q = chess.Board("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")
    b_minus_q = chess.Board("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNB1KBNR w KQkq - 0 1")

    cp_start = simulate_csharp_standard(ft_weights, ft_biases, hidden_weights, hidden_biases, output_weights, output_bias_val, *encode_board_features(b_start))
    cp_plus_q = simulate_csharp_standard(ft_weights, ft_biases, hidden_weights, hidden_biases, output_weights, output_bias_val, *encode_board_features(b_plus_q))
    cp_minus_q = simulate_csharp_standard(ft_weights, ft_biases, hidden_weights, hidden_biases, output_weights, output_bias_val, *encode_board_features(b_minus_q))

    print(f"Successfully exported Standard NNUE to: {output_path} ({os.path.getsize(output_path)} bytes)")
    print(f"  [Quantized C# Eval Check] StartPos: {cp_start:+d} cp | +Queen: {cp_plus_q:+d} cp | -Queen: {cp_minus_q:+d} cp")


def export_micro(model: MicroNNUE, output_path: str):
    """Exports MicroNNUE (768 -> 16x2 -> 1) to binary and verifies quantization parity."""
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    clamp_nnue_weights(model)

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

    b_start = chess.Board(chess.STARTING_FEN)
    b_plus_q = chess.Board("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")
    b_minus_q = chess.Board("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNB1KBNR w KQkq - 0 1")

    cp_start = simulate_csharp_micro(ft_weights, ft_biases, output_weights, output_bias_val, *encode_board_features(b_start))
    cp_plus_q = simulate_csharp_micro(ft_weights, ft_biases, output_weights, output_bias_val, *encode_board_features(b_plus_q))
    cp_minus_q = simulate_csharp_micro(ft_weights, ft_biases, output_weights, output_bias_val, *encode_board_features(b_minus_q))

    print(f"Successfully exported Micro NNUE to: {output_path} ({os.path.getsize(output_path)} bytes)")
    print(f"  [Quantized C# Eval Check] StartPos: {cp_start:+d} cp | +Queen: {cp_plus_q:+d} cp | -Queen: {cp_minus_q:+d} cp")


def main():
    parser = argparse.ArgumentParser(description="Export PyTorch NNUE checkpoint to quantized binary .nnue")
    parser.add_argument("--checkpoint", type=str, required=True, help="Path to input PyTorch checkpoint (.pt)")
    parser.add_argument(
        "--model",
        type=str,
        choices=["open", "closed", "endgame", "queenless", "imbalance", "attack", "micro", "small", "volatility"],
        required=True,
        help="Model architecture type (open, closed, endgame, queenless, imbalance, attack, micro/small, volatility)",
    )
    parser.add_argument("--output", type=str, default=None, help="Output .nnue binary file path")
    args = parser.parse_args()

    model_name = "micro" if args.model == "small" else args.model
    output_file = args.output or f"./networks/{model_name}.nnue"

    if args.model in ("micro", "small", "volatility"):
        net = MicroNNUE()
        raw = torch.load(args.checkpoint, map_location="cpu")
        sd = raw["model_state_dict"] if isinstance(raw, dict) and "model_state_dict" in raw else raw
        net.load_state_dict(sd)
        net.eval()
        export_micro(net, output_file)
    else:
        net = StandardNNUE()
        raw = torch.load(args.checkpoint, map_location="cpu")
        sd = raw["model_state_dict"] if isinstance(raw, dict) and "model_state_dict" in raw else raw
        net.load_state_dict(sd)
        net.eval()
        export_standard(net, output_file)


if __name__ == "__main__":
    main()
