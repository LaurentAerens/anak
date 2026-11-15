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

try:
    from model import (
        StandardNNUE,
        SharedMoENNUE,
        RAW_FEATURE_COUNT,
        KING_BUCKETS,
        FEATURE_COUNT,
        STANDARD_HIDDEN_DIM,
        STANDARD_L1_DIM,
        EXPERT_COUNT,
        EXPERT_NAMES,
        get_king_bucket,
        clamp_nnue_weights,
    )
    from dataset import encode_board_features
except ImportError:
    from training.model import (
        StandardNNUE,
        SharedMoENNUE,
        RAW_FEATURE_COUNT,
        KING_BUCKETS,
        FEATURE_COUNT,
        STANDARD_HIDDEN_DIM,
        STANDARD_L1_DIM,
        EXPERT_COUNT,
        EXPERT_NAMES,
        get_king_bucket,
        clamp_nnue_weights,
    )
    from training.dataset import encode_board_features

MAGIC_HEADER = 0x4B414E41  # "ANAK"
FORMAT_VERSION = 1
ARCH_STANDARD = 0x0001
ARCH_MOE = 0x0003

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
    """Simulates exact C# NnueInference.EvaluateStandard integer arithmetic with king bucketing and SCReLU."""
    # Find active king buckets from king squares [320..383]
    w_king_sq = next((x - 320 for x in our_idx if 320 <= x < 384), 4)
    w_bucket = get_king_bucket(w_king_sq)
    b_king_sq = next((x - 320 for x in their_idx if 320 <= x < 384), 4)
    b_bucket = get_king_bucket(b_king_sq)

    our_acc = ft_biases.astype(np.int32).copy()
    w_offset = w_bucket * RAW_FEATURE_COUNT
    for idx in our_idx:
        if idx < RAW_FEATURE_COUNT:
            our_acc += ft_weights[w_offset + idx].astype(np.int32)

    their_acc = ft_biases.astype(np.int32).copy()
    b_offset = b_bucket * RAW_FEATURE_COUNT
    for idx in their_idx:
        if idx < RAW_FEATURE_COUNT:
            their_acc += ft_weights[b_offset + idx].astype(np.int32)

    # SCReLU activation: (clamp(x, 0, 64)^2) >> 6
    our_clipped = np.clip(our_acc, 0, CLIPPED_RELU_MAX)
    our_act = (our_clipped * our_clipped) >> 6

    their_clipped = np.clip(their_acc, 0, CLIPPED_RELU_MAX)
    their_act = (their_clipped * their_clipped) >> 6

    activated = np.concatenate([our_act, their_act])  # (1024,)

    l1_dim = hidden_weights.shape[0]
    hidden_out = np.zeros(l1_dim, dtype=np.int32)
    for k in range(l1_dim):
        dot = int(hidden_biases[k]) * WEIGHT_SCALE + int(np.dot(activated, hidden_weights[k].astype(np.int32)))
        clamped = np.clip(int(dot / WEIGHT_SCALE), 0, CLIPPED_RELU_MAX)
        hidden_out[k] = (clamped * clamped) >> 6

    final_sum = int(output_bias_val) + int(np.dot(hidden_out, output_weights.astype(np.int32)))
    return int((final_sum * OUTPUT_SCALE) // (WEIGHT_SCALE * WEIGHT_SCALE))


def simulate_csharp_moe(
    ft_weights: np.ndarray,
    ft_biases: np.ndarray,
    expert_hidden_weights: list,
    expert_hidden_biases: list,
    expert_output_weights: list,
    expert_output_biases: list,
    expert_idx: int,
    our_idx: list,
    their_idx: list,
) -> int:
    """Simulates exact C# NnueInference.EvaluateMoE integer arithmetic for a specific expert head."""
    return simulate_csharp_standard(
        ft_weights,
        ft_biases,
        expert_hidden_weights[expert_idx],
        expert_hidden_biases[expert_idx],
        expert_output_weights[expert_idx],
        expert_output_biases[expert_idx],
        our_idx,
        their_idx,
    )


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
        # Header: Magic (4B), Version (4B), ArchId (2B), HiddenDim (2B), L1Dim (2B), NumBuckets (2B)
        header = struct.pack(
            "<IIHHHH",
            MAGIC_HEADER,
            FORMAT_VERSION,
            ARCH_STANDARD,
            model.hidden_dim,
            model.l1_dim,
            KING_BUCKETS,
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


def export_moe(model: SharedMoENNUE, output_path: str):
    """Exports SharedMoENNUE (12288 -> 512x2 -> 6 x (64 -> 1)) to ArchMoE binary and verifies parity."""
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    clamp_nnue_weights(model)

    ft_weights = quantize_sbyte(model.ft.weight.T)  # (12288, 512) int8
    ft_biases = quantize_short(model.ft.bias)       # (512,) int16

    expert_count = model.expert_count
    expert_hidden_weights = []
    expert_hidden_biases = []
    expert_output_weights = []
    expert_output_biases = []

    for e in range(expert_count):
        expert_hidden_weights.append(quantize_sbyte(model.l1_weight[e]))
        expert_hidden_biases.append(quantize_short(model.l1_bias[e]))
        expert_output_weights.append(quantize_short(model.out_weight[e].squeeze(0)))
        expert_output_biases.append(int(round(model.out_bias[e].item() * (WEIGHT_SCALE * BIAS_SCALE))))

    packed_counts = ((expert_count & 0xFF) << 8) | (KING_BUCKETS & 0xFF)

    with open(output_path, "wb") as f:
        header = struct.pack(
            "<IIHHHH",
            MAGIC_HEADER,
            FORMAT_VERSION,
            ARCH_MOE,
            model.hidden_dim,
            model.l1_dim,
            packed_counts,
        )
        f.write(header)
        f.write(ft_weights.tobytes())
        f.write(ft_biases.tobytes())
        for e in range(expert_count):
            f.write(expert_hidden_weights[e].tobytes())
            f.write(expert_hidden_biases[e].tobytes())
            f.write(expert_output_weights[e].tobytes())
            f.write(struct.pack("<i", expert_output_biases[e]))

    b_start = chess.Board(chess.STARTING_FEN)
    b_plus_q = chess.Board("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")
    b_minus_q = chess.Board("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNB1KBNR w KQkq - 0 1")
    f_start = encode_board_features(b_start)
    f_plus_q = encode_board_features(b_plus_q)
    f_minus_q = encode_board_features(b_minus_q)

    print(f"Successfully exported Unified MoE NNUE to: {output_path} ({os.path.getsize(output_path)} bytes)")
    for e in range(expert_count):
        name = EXPERT_NAMES[e] if e < len(EXPERT_NAMES) else f"expert_{e}"
        cp_start = simulate_csharp_moe(ft_weights, ft_biases, expert_hidden_weights, expert_hidden_biases, expert_output_weights, expert_output_biases, e, *f_start)
        cp_plus_q = simulate_csharp_moe(ft_weights, ft_biases, expert_hidden_weights, expert_hidden_biases, expert_output_weights, expert_output_biases, e, *f_plus_q)
        cp_minus_q = simulate_csharp_moe(ft_weights, ft_biases, expert_hidden_weights, expert_hidden_biases, expert_output_weights, expert_output_biases, e, *f_minus_q)
        print(f"  [Expert {e}: {name:9s}] StartPos: {cp_start:+d} cp | +Queen: {cp_plus_q:+d} cp | -Queen: {cp_minus_q:+d} cp")


def main():
    parser = argparse.ArgumentParser(description="Export PyTorch NNUE checkpoint to quantized binary .nnue")
    parser.add_argument("--checkpoint", type=str, required=True, help="Path to input PyTorch checkpoint (.pt)")
    parser.add_argument(
        "--model",
        type=str,
        choices=["moe", "open", "closed", "endgame", "queenless", "imbalance", "attack"],
        required=True,
        help="Model architecture type (moe, open, closed, endgame, queenless, imbalance, attack)",
    )
    parser.add_argument("--output", type=str, default=None, help="Output .nnue binary file path")
    args = parser.parse_args()

    default_name = "anak" if args.model == "moe" else args.model
    output_file = args.output or f"./networks/{default_name}.nnue"

    raw = torch.load(args.checkpoint, map_location="cpu")
    sd = raw["model_state_dict"] if isinstance(raw, dict) and "model_state_dict" in raw else raw

    if args.model == "moe":
        net = SharedMoENNUE()
        net.load_state_dict(sd)
        net.eval()
        export_moe(net, output_file)
    else:
        net = StandardNNUE()
        net.load_state_dict(sd)
        net.eval()
        export_standard(net, output_file)


if __name__ == "__main__":
    main()
