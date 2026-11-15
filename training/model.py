"""
Enhanced Anak NNUE Neural Network Architectures (PyTorch).
Defines StandardNNUE (12,288 -> 512x2 -> 64 -> 1) and SharedMoENNUE
(Shared 12,288 -> 512x2 Feature Transformer + 6 x [1024 -> 64 -> 1] Expert Heads).
All weights and piece-square features are 100% trainable from data, with quantization-aware
weight clamping, perspective-twin antisymmetry, and step-aware Model EMA with warmup.
"""

from typing import Dict, List, Optional, Tuple
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

RAW_FEATURE_COUNT = 768  # 6 piece types * 2 colors * 64 squares
KING_BUCKETS = 16        # 16 king safety zones with horizontal symmetry (ranks 0-1, 2-3, 4-5, 6-7 x files A-D)
FEATURE_COUNT = KING_BUCKETS * RAW_FEATURE_COUNT  # 12,288 bucketed HalfKP features
STANDARD_HIDDEN_DIM = 512
STANDARD_L1_DIM = 64
EXPERT_COUNT = 6
EXPERT_NAMES = ("open", "closed", "endgame", "queenless", "imbalance", "attack")
ARCH_STANDARD = 0x0001
ARCH_MOE = 0x0003


class ClippedReLU(nn.Module):
    """Clipped ReLU activation: clamp(x, 0, 1.0) for normalized training."""
    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return torch.clamp(x, 0.0, 1.0)


class SCReLU(nn.Module):
    """Squared Clipped ReLU activation: (clamp(x, 0, 1.0))^2 for second-order feature interactions."""
    def forward(self, x: torch.Tensor) -> torch.Tensor:
        c = torch.clamp(x, 0.0, 1.0)
        return c * c


def get_king_bucket(king_square: int) -> int:
    """Computes the 16-bucket king safety zone index with horizontal reflection (files E-H -> D-A)."""
    f = king_square & 7
    r = king_square >> 3
    folded_file = 7 - f if f >= 4 else f  # 0..3
    rank_group = r >> 1                   # 0..3 (ranks 0-1 -> 0, 2-3 -> 1, 4-5 -> 2, 6-7 -> 3)
    return (rank_group << 2) | folded_file


def map_raw_indices_to_buckets(indices: torch.Tensor) -> torch.Tensor:
    """
    Vectorized mapping of raw HalfKP piece indices [0..767] (with sentinel 0xFFFF / 65535)
    to 16-bucket indices [0..12287] based on friendly king square [320..383].
    """
    mask = (indices >= 320) & (indices < 384)
    king_sq = (torch.where(mask, indices, torch.zeros_like(indices))).sum(dim=-1) - 320
    king_sq = torch.clamp(king_sq, 0, 63)

    f = king_sq & 7
    r = king_sq >> 3
    folded_file = torch.where(f >= 4, 7 - f, f)
    rank_group = r >> 1
    bucket = (rank_group << 2) | folded_file

    offset = (bucket * RAW_FEATURE_COUNT).unsqueeze(-1)
    return torch.where(indices < RAW_FEATURE_COUNT, indices + offset, FEATURE_COUNT)


def enforce_perspective_antisymmetry(model: nn.Module):
    """
    Projects the final hidden/output layer pairs onto the perspective-antisymmetric manifold
    so that evaluating (our_features, their_features) vs (their_features, our_features) is
    always consistent and symmetric positions never suffer from arbitrary constant bias drift.
    Does not freeze or overwrite any feature-transformer weights.
    """
    with torch.no_grad():
        hidden_dim = model.hidden_dim
        if hasattr(model, "l1_weight"):
            # SharedMoENNUE: (E, l1_dim, 2 * hidden_dim)
            l1_dim = model.l1_weight.shape[1]
            half_l1 = l1_dim // 2
            w_our = 0.5 * (model.l1_weight[:, :half_l1, :hidden_dim] + model.l1_weight[:, half_l1:l1_dim, hidden_dim:])
            w_their = 0.5 * (model.l1_weight[:, :half_l1, hidden_dim:] + model.l1_weight[:, half_l1:l1_dim, :hidden_dim])
            model.l1_weight[:, :half_l1, :hidden_dim].copy_(w_our)
            model.l1_weight[:, :half_l1, hidden_dim:].copy_(w_their)
            model.l1_weight[:, half_l1:l1_dim, :hidden_dim].copy_(w_their)
            model.l1_weight[:, half_l1:l1_dim, hidden_dim:].copy_(w_our)

            b_twin = 0.5 * (model.l1_bias[:, :half_l1] + model.l1_bias[:, half_l1:l1_dim])
            model.l1_bias[:, :half_l1].copy_(b_twin)
            model.l1_bias[:, half_l1:l1_dim].copy_(b_twin)

            out_twin = 0.5 * (model.out_weight[:, 0, :half_l1] - model.out_weight[:, 0, half_l1:l1_dim])
            model.out_weight[:, 0, :half_l1].copy_(out_twin)
            model.out_weight[:, 0, half_l1:l1_dim].copy_(-out_twin)
            model.out_bias.clamp_(0.15, 0.25)
        elif hasattr(model, "l1"):
            l1_dim = model.l1.weight.shape[0]
            half_l1 = l1_dim // 2
            w_our = 0.5 * (model.l1.weight[:half_l1, :hidden_dim] + model.l1.weight[half_l1:l1_dim, hidden_dim:])
            w_their = 0.5 * (model.l1.weight[:half_l1, hidden_dim:] + model.l1.weight[half_l1:l1_dim, :hidden_dim])
            model.l1.weight[:half_l1, :hidden_dim].copy_(w_our)
            model.l1.weight[:half_l1, hidden_dim:].copy_(w_their)
            model.l1.weight[half_l1:l1_dim, :hidden_dim].copy_(w_their)
            model.l1.weight[half_l1:l1_dim, hidden_dim:].copy_(w_our)

            b_twin = 0.5 * (model.l1.bias[:half_l1] + model.l1.bias[half_l1:l1_dim])
            model.l1.bias[:half_l1].copy_(b_twin)
            model.l1.bias[half_l1:l1_dim].copy_(b_twin)

            out_twin = 0.5 * (model.output.weight[0, :half_l1] - model.output.weight[0, half_l1:l1_dim])
            model.output.weight[0, :half_l1].copy_(out_twin)
            model.output.weight[0, half_l1:l1_dim].copy_(-out_twin)
            model.output.bias.clamp_(0.15, 0.25)


def init_nnue_weights(model: nn.Module):
    """
    Clean, 100% trainable NNUE weight initialization.
    Warm-starts square-uniform material counting on the first 6 channels at step 0
    across all king buckets (without hardcoded piece-square tables) so the optimizer
    learns all positional and tactical features directly from data.
    """
    with torch.no_grad():
        for name, param in model.named_parameters():
            if "ft.weight" in name:
                nn.init.normal_(param, mean=0.0, std=0.04)
            elif "ft.bias" in name:
                nn.init.constant_(param, 0.15)
            elif "l1.weight" in name or "l1_weight" in name:
                nn.init.normal_(param, mean=0.0, std=0.06)
            elif "l1.bias" in name or "l1_bias" in name:
                nn.init.constant_(param, 0.10)
            elif "output.weight" in name or "out_weight" in name:
                nn.init.normal_(param, mean=0.0, std=0.05)
            elif "output.bias" in name or "out_bias" in name:
                nn.init.constant_(param, 0.20)

        # Warm-start square-uniform material across all buckets
        model.ft.weight[:6, :].zero_()
        model.ft.bias[:6].zero_()
        num_buckets = max(1, model.ft.weight.shape[1] // RAW_FEATURE_COUNT)

        pawn_val = 0.45
        knight_val = 0.57
        bishop_val = 0.57
        rook_val = 0.69
        queen_val = 0.95

        for b in range(num_buckets):
            b_offset = b * RAW_FEATURE_COUNT
            for sq in range(64):
                ch_pawn = 0 if (sq % 8) < 4 else 1
                model.ft.weight[ch_pawn, b_offset + 0 * 64 + sq] = pawn_val
                model.ft.weight[2, b_offset + 1 * 64 + sq] = knight_val
                model.ft.weight[3, b_offset + 2 * 64 + sq] = bishop_val
                model.ft.weight[4, b_offset + 3 * 64 + sq] = rook_val
                model.ft.weight[5, b_offset + 4 * 64 + sq] = queen_val

        scales = torch.tensor(
            [5.0, 5.0, 10.0, 10.0, 10.4167, 10.0],
            device=model.ft.weight.device,
            dtype=model.ft.weight.dtype,
        )
        hidden_dim = model.hidden_dim
        if hasattr(model, "l1_weight"):
            l1_dim = model.l1_weight.shape[1]
            half_l1 = l1_dim // 2
            model.l1_weight[:, :6, :].zero_()
            model.l1_weight[:, half_l1:half_l1 + 6, :].zero_()
            model.l1_bias[:, :6].zero_()
            model.l1_bias[:, half_l1:half_l1 + 6].zero_()
            for c in range(6):
                model.l1_weight[:, c, c] = 1.0
                model.l1_weight[:, half_l1 + c, hidden_dim + c] = 1.0
            model.out_weight[:, 0, :6] = scales
            model.out_weight[:, 0, half_l1:half_l1 + 6] = -scales
        elif hasattr(model, "l1"):
            l1_dim = model.l1.weight.shape[0]
            half_l1 = l1_dim // 2
            model.l1.weight[:6, :].zero_()
            model.l1.weight[half_l1:half_l1 + 6, :].zero_()
            model.l1.bias[:6].zero_()
            model.l1.bias[half_l1:half_l1 + 6].zero_()
            for c in range(6):
                model.l1.weight[c, c] = 1.0
                model.l1.weight[half_l1 + c, hidden_dim + c] = 1.0
            model.output.weight[0, :6] = scales
            model.output.weight[0, half_l1:half_l1 + 6] = -scales

        enforce_perspective_antisymmetry(model)


def clamp_nnue_weights(model: nn.Module, max_val: float = 1.98, enforce_symmetry: bool = False):
    """
    Quantization-aware weight clamping to ensure zero precision loss
    when ft.weight and l1.weight are quantized to INT8 [-128, 127] with factor 64,
    while keeping all weights trainable.
    """
    with torch.no_grad():
        for name, param in model.named_parameters():
            if "ft.weight" in name or "l1.weight" in name or "l1_weight" in name:
                param.clamp_(-max_val, max_val)
            elif "output.weight" in name or "out_weight" in name:
                # output.weight is quantized to INT16 [-32768, 32767] with factor 64
                param.clamp_(-250.0, 250.0)

        if enforce_symmetry:
            enforce_perspective_antisymmetry(model)


class ModelEMA:
    """
    Exponential Moving Average (Polyak Averaging) for NNUE models with step-aware warmup.
    Uses dynamic decay = min(base_decay, (1 + step) / (10 + step)) so early epochs and
    small/medium datasets never remain stuck near initialization.
    """
    def __init__(self, model: nn.Module, decay: float = 0.999):
        self.decay = decay
        self.num_updates = 0
        self.shadow = {
            name: param.clone().detach()
            for name, param in model.named_parameters()
            if param.requires_grad
        }

    def update(self, model: nn.Module):
        """Updates shadow weights with current model weights using warmup-adjusted decay."""
        self.num_updates += 1
        effective_decay = min(self.decay, (1.0 + self.num_updates) / (10.0 + self.num_updates))
        with torch.no_grad():
            for name, param in model.named_parameters():
                if name in self.shadow:
                    self.shadow[name].mul_(effective_decay).add_(param.data, alpha=1.0 - effective_decay)

    def apply_shadow(self, model: nn.Module):
        """Copies shadow weights into the model for evaluation or export."""
        with torch.no_grad():
            for name, param in model.named_parameters():
                if name in self.shadow:
                    param.data.copy_(self.shadow[name])
        enforce_perspective_antisymmetry(model)

    def store_and_apply(self, model: nn.Module) -> Dict[str, torch.Tensor]:
        """Temporarily applies shadow weights and returns a backup of the original weights."""
        backup = {name: param.clone().detach() for name, param in model.named_parameters()}
        self.apply_shadow(model)
        return backup

    def restore(self, model: nn.Module, backup: Dict[str, torch.Tensor]):
        """Restores original training weights from backup."""
        with torch.no_grad():
            for name, param in model.named_parameters():
                if name in backup:
                    param.data.copy_(backup[name])


class StandardNNUE(nn.Module):
    """
    Standard King-Bucketed NNUE Architecture for the expert models:
    12,288 sparse features (16 king buckets x 768) -> 512 (per perspective) -> 1024 combined -> 64 -> 1.
    SCReLU activation: (clamp(x, 0, 1.0))^2.
    """
    def __init__(self, hidden_dim: int = STANDARD_HIDDEN_DIM, l1_dim: int = STANDARD_L1_DIM):
        super().__init__()
        self.hidden_dim = hidden_dim
        self.l1_dim = l1_dim
        self.arch_id = 0x0001

        # Feature Transformer (16 king buckets x 768 HalfKP features)
        self.ft = nn.Linear(FEATURE_COUNT, hidden_dim)

        # Hidden layer: 2 * hidden_dim -> l1_dim
        self.l1 = nn.Linear(hidden_dim * 2, l1_dim)

        # Output layer: l1_dim -> 1
        self.output = nn.Linear(l1_dim, 1)

        self.act = SCReLU()
        init_nnue_weights(self)

    def forward(self, our_features: torch.Tensor, their_features: torch.Tensor) -> torch.Tensor:
        """
        Forward pass with king-bucketed SCReLU evaluation.
        Supports both:
        1. Sparse indices of shape (batch_size, <= 64) with raw indices in [0..767].
           Maps to active king bucket [0..15] dynamically via map_raw_indices_to_buckets.
        2. Dense float tensor of shape (batch_size, 12288) or (batch_size, 768).
        :return: Scalar evaluation value in pawns (1.0 = 100 centipawns).
        """
        if our_features.dtype in (torch.int16, torch.int32, torch.int64):
            our_idx = map_raw_indices_to_buckets(our_features.to(torch.int64))
            their_idx = map_raw_indices_to_buckets(their_features.to(torch.int64))
            pad = torch.zeros(1, self.hidden_dim, device=self.ft.weight.device, dtype=self.ft.weight.dtype)
            w_padded = torch.cat([self.ft.weight.t(), pad], dim=0)
            our_acc = self.act(F.embedding_bag(our_idx, w_padded, mode="sum") + self.ft.bias)
            their_acc = self.act(F.embedding_bag(their_idx, w_padded, mode="sum") + self.ft.bias)
        else:
            if our_features.shape[-1] == RAW_FEATURE_COUNT:
                offset = 3 * RAW_FEATURE_COUNT
                our_dense = torch.zeros(our_features.shape[0], FEATURE_COUNT, device=our_features.device, dtype=our_features.dtype)
                their_dense = torch.zeros(their_features.shape[0], FEATURE_COUNT, device=their_features.device, dtype=their_features.dtype)
                our_dense[:, offset:offset + RAW_FEATURE_COUNT] = our_features
                their_dense[:, offset:offset + RAW_FEATURE_COUNT] = their_features
                our_acc = self.act(self.ft(our_dense))
                their_acc = self.act(self.ft(their_dense))
            else:
                our_acc = self.act(self.ft(our_features))
                their_acc = self.act(self.ft(their_features))

        combined = torch.cat([our_acc, their_acc], dim=-1)
        hidden = self.act(self.l1(combined))
        out = self.output(hidden)
        return out.squeeze(-1)


class SharedMoENNUE(nn.Module):
    """
    Shared Feature-Transformer Multi-Expert NNUE Architecture (ArchMoE = 0x0003):
    12,288 sparse features (16 king buckets x 768) -> 512x2 (shared FT) -> 6 x [1024 -> 64 -> 1] expert heads.
    Supports leaf-level expert routing and smooth Open/Closed boundary interpolation.
    """
    def __init__(
        self,
        hidden_dim: int = STANDARD_HIDDEN_DIM,
        l1_dim: int = STANDARD_L1_DIM,
        expert_count: int = EXPERT_COUNT,
    ):
        super().__init__()
        self.hidden_dim = hidden_dim
        self.l1_dim = l1_dim
        self.expert_count = expert_count
        self.arch_id = ARCH_MOE

        # Shared Feature Transformer (16 king buckets x 768 HalfKP features)
        self.ft = nn.Linear(FEATURE_COUNT, hidden_dim)

        # 6 Specialized Expert Heads: (E, L1Dim, 2*HiddenDim) -> (E, 1, L1Dim)
        self.l1_weight = nn.Parameter(torch.empty(expert_count, l1_dim, hidden_dim * 2))
        self.l1_bias = nn.Parameter(torch.empty(expert_count, l1_dim))
        self.out_weight = nn.Parameter(torch.empty(expert_count, 1, l1_dim))
        self.out_bias = nn.Parameter(torch.empty(expert_count, 1))

        self.act = SCReLU()
        init_nnue_weights(self)

    def encode_shared_features(self, our_features: torch.Tensor, their_features: torch.Tensor) -> torch.Tensor:
        """Computes the shared SCReLU-activated (batch_size, 2 * hidden_dim) representation once."""
        if our_features.dtype in (torch.int16, torch.int32, torch.int64):
            our_idx = map_raw_indices_to_buckets(our_features.to(torch.int64))
            their_idx = map_raw_indices_to_buckets(their_features.to(torch.int64))
            pad = torch.zeros(1, self.hidden_dim, device=self.ft.weight.device, dtype=self.ft.weight.dtype)
            w_padded = torch.cat([self.ft.weight.t(), pad], dim=0)
            our_acc = self.act(F.embedding_bag(our_idx, w_padded, mode="sum") + self.ft.bias)
            their_acc = self.act(F.embedding_bag(their_idx, w_padded, mode="sum") + self.ft.bias)
        else:
            if our_features.shape[-1] == RAW_FEATURE_COUNT:
                offset = 3 * RAW_FEATURE_COUNT
                our_dense = torch.zeros(our_features.shape[0], FEATURE_COUNT, device=our_features.device, dtype=our_features.dtype)
                their_dense = torch.zeros(their_features.shape[0], FEATURE_COUNT, device=their_features.device, dtype=their_features.dtype)
                our_dense[:, offset:offset + RAW_FEATURE_COUNT] = our_features
                their_dense[:, offset:offset + RAW_FEATURE_COUNT] = their_features
                our_acc = self.act(self.ft(our_dense))
                their_acc = self.act(self.ft(their_dense))
            else:
                our_acc = self.act(self.ft(our_features))
                their_acc = self.act(self.ft(their_features))

        return torch.cat([our_acc, their_acc], dim=-1)

    def forward(
        self,
        our_features: torch.Tensor,
        their_features: torch.Tensor,
        expert_ids: Optional[torch.Tensor] = None,
        openness: Optional[torch.Tensor] = None,
        open_threshold: float = 0.49,
    ) -> torch.Tensor:
        """
        Forward pass with shared FT and fused multi-head evaluation.
        If expert_ids is provided, routes each sample to its corresponding expert head.
        If openness is also provided, smoothly blends Open (0) and Closed (1) in [open_threshold - 0.08, open_threshold + 0.08].
        """
        combined = self.encode_shared_features(our_features, their_features)  # (B, 1024)

        # Fused evaluation of all 6 heads: (B, 1024) @ (1024, 6*64) -> (B, 6, 64)
        w_flat = self.l1_weight.reshape(self.expert_count * self.l1_dim, self.hidden_dim * 2)
        all_l1 = F.linear(combined, w_flat, self.l1_bias.reshape(-1)).view(-1, self.expert_count, self.l1_dim)
        all_hidden = self.act(all_l1)  # (B, 6, 64)

        # (B, 6, 64) * (6, 64) -> sum -> (B, 6)
        all_out = (all_hidden * self.out_weight.squeeze(1).unsqueeze(0)).sum(dim=-1) + self.out_bias.squeeze(-1).unsqueeze(0)

        if expert_ids is None:
            return all_out[:, 0]

        idx = expert_ids.to(torch.int64).clamp(0, self.expert_count - 1)
        preds = all_out.gather(1, idx.unsqueeze(1)).squeeze(1)

        if openness is not None:
            low = open_threshold - 0.08
            high = open_threshold + 0.08
            in_band = ((idx == 0) | (idx == 1)) & (openness > low) & (openness < high)
            if in_band.any():
                alpha = torch.clamp((openness - low) / 0.16, 0.0, 1.0)
                blended = ((1.0 - alpha) * all_out[:, 1]) + (alpha * all_out[:, 0])
                preds = torch.where(in_band, blended, preds)

        return preds


BENCHMARK_POSITIONS: List[Tuple[str, str]] = [
    ("StartPos", "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"),
    ("Tactical Refutation", "r1bqkb1r/pppp1ppp/2n5/4p3/2B1n3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 5"),
    ("KRvK Endgame", "8/8/8/4k3/8/8/4K3/R7 w - - 0 1"),
    ("Symmetric Draw", "r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/2N2N2/PPPP1PPP/R1BQK2R w KQkq - 4 5"),
]


def evaluate_benchmark_positions(model: nn.Module, device: torch.device) -> str:
    """
    Evaluates standard chess benchmark positions on the model.
    Returns a formatted one-line string with centipawn predictions for terminal logging.
    """
    import chess
    try:
        from binpack import encode_features_to_indices
    except ImportError:
        from training.binpack import encode_features_to_indices

    model.eval()
    results = []
    with torch.inference_mode():
        for label, fen in BENCHMARK_POSITIONS:
            board = chess.Board(fen)
            our_idx, their_idx, _ = encode_features_to_indices(board)
            our_t = torch.from_numpy(np.array([our_idx], dtype=np.int32)).to(device)
            their_t = torch.from_numpy(np.array([their_idx], dtype=np.int32)).to(device)
            pred_pawns = model(our_t, their_t).item()
            pred_cp = int(round(pred_pawns * 100))
            results.append(f"{label}: {pred_cp:+d}cp")
    return " | ".join(results)
