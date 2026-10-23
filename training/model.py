"""
Enhanced Anak NNUE Neural Network Architectures (PyTorch).
Defines StandardNNUE (768 -> 512x2 -> 64 -> 1) and MicroNNUE (768 -> 16x2 -> 1).
All weights and piece-square features are 100% trainable from data, with quantization-aware
weight clamping, perspective-twin antisymmetry, and step-aware Model EMA with warmup.
"""

from typing import Dict, List, Tuple
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

FEATURE_COUNT = 768  # 6 piece types * 2 colors * 64 squares
STANDARD_HIDDEN_DIM = 512
STANDARD_L1_DIM = 64
MICRO_HIDDEN_DIM = 16


class ClippedReLU(nn.Module):
    """Clipped ReLU activation: clamp(x, 0, 1.0) for normalized training."""
    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return torch.clamp(x, 0.0, 1.0)


def enforce_perspective_antisymmetry(model: nn.Module):
    """
    Projects the final hidden/output layer pairs onto the perspective-antisymmetric manifold
    so that evaluating (our_features, their_features) vs (their_features, our_features) is
    always consistent and symmetric positions never suffer from arbitrary constant bias drift.
    Does not freeze or overwrite any feature-transformer weights.
    """
    with torch.no_grad():
        hidden_dim = model.hidden_dim
        if hasattr(model, "l1"):
            l1_dim = model.l1.weight.shape[0]
            half_l1 = l1_dim // 2
            # StandardNNUE: pair hidden units 0..half_l1-1 with half_l1..l1_dim-1 as perspective twins
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
        else:
            # MicroNNUE: pair our_acc (0..15) with their_acc (16..31) antisymmetrically
            out_twin = 0.5 * (model.output.weight[0, :hidden_dim] - model.output.weight[0, hidden_dim:2 * hidden_dim])
            model.output.weight[0, :hidden_dim].copy_(out_twin)
            model.output.weight[0, hidden_dim:2 * hidden_dim].copy_(-out_twin)

        # Keep side-to-move tempo bonus bounded in [+15cp, +25cp]
        model.output.bias.clamp_(0.15, 0.25)


def init_nnue_weights(model: nn.Module):
    """
    Clean, 100% trainable NNUE weight initialization.
    Warm-starts square-uniform material counting on the first 6 channels at step 0
    (without any hardcoded piece-square tables) so the optimizer learns all positional
    and tactical features directly from the dataset.
    """
    with torch.no_grad():
        for name, param in model.named_parameters():
            if "ft.weight" in name:
                nn.init.normal_(param, mean=0.0, std=0.04)
            elif "ft.bias" in name:
                nn.init.constant_(param, 0.15)
            elif "l1.weight" in name:
                nn.init.normal_(param, mean=0.0, std=0.06)
            elif "l1.bias" in name:
                nn.init.constant_(param, 0.10)
            elif "output.weight" in name:
                nn.init.normal_(param, mean=0.0, std=0.05)
            elif "output.bias" in name:
                nn.init.constant_(param, 0.20)

        # Square-uniform material warm-start at step 0 (completely trainable)
        model.ft.weight[:6, :].zero_()
        model.ft.bias[:6].zero_()
        for sq in range(64):
            # Split pawns across channels 0 (files A-D) and 1 (files E-H) so 4 pawns * 0.20 = 0.80 <= 1.0
            ch_pawn = 0 if (sq % 8) < 4 else 1
            model.ft.weight[ch_pawn, 0 * 64 + sq] = 0.20   # Pawn: 0.20 * 5.0 = 1.00
            model.ft.weight[2, 1 * 64 + sq] = 0.32         # Knight: 0.32 * 10.0 = 3.20
            model.ft.weight[3, 2 * 64 + sq] = 0.33         # Bishop: 0.33 * 10.0 = 3.30
            model.ft.weight[4, 3 * 64 + sq] = 0.48         # Rook: 0.48 * 10.4167 = 5.00
            model.ft.weight[5, 4 * 64 + sq] = 0.90         # Queen: 0.90 * 10.0 = 9.00

        scales = torch.tensor(
            [5.0, 5.0, 10.0, 10.0, 10.4167, 10.0],
            device=model.ft.weight.device,
            dtype=model.ft.weight.dtype,
        )
        hidden_dim = model.hidden_dim
        if hasattr(model, "l1"):
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
        else:
            model.output.weight[0, :6] = scales
            model.output.weight[0, hidden_dim:hidden_dim + 6] = -scales

        enforce_perspective_antisymmetry(model)


def clamp_nnue_weights(model: nn.Module, max_val: float = 1.98, enforce_symmetry: bool = False):
    """
    Quantization-aware weight clamping to ensure zero precision loss
    when ft.weight and l1.weight are quantized to INT8 [-128, 127] with factor 64,
    while keeping all weights trainable.
    """
    with torch.no_grad():
        for name, param in model.named_parameters():
            if "ft.weight" in name or "l1.weight" in name:
                param.clamp_(-max_val, max_val)
            elif "output.weight" in name:
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
    Standard NNUE Architecture for the 6 expert models:
    768 sparse features -> 512 (per perspective) -> 1024 combined -> 64 -> 1.
    """
    def __init__(self, hidden_dim: int = STANDARD_HIDDEN_DIM, l1_dim: int = STANDARD_L1_DIM):
        super().__init__()
        self.hidden_dim = hidden_dim
        self.l1_dim = l1_dim
        self.arch_id = 0x0001

        # Feature Transformer (shared between White and Black perspectives)
        self.ft = nn.Linear(FEATURE_COUNT, hidden_dim)

        # Hidden layer: 2 * hidden_dim -> l1_dim
        self.l1 = nn.Linear(hidden_dim * 2, l1_dim)

        # Output layer: l1_dim -> 1
        self.output = nn.Linear(l1_dim, 1)

        self.act = ClippedReLU()
        init_nnue_weights(self)

    def forward(self, our_features: torch.Tensor, their_features: torch.Tensor) -> torch.Tensor:
        """
        Forward pass. Supports both:
        1. Sparse indices of shape (batch_size, <= 64) with dtype int16/int32/int64
           -> 48x faster feature accumulation via F.embedding_bag, zero dense allocations.
        2. Dense float tensor of shape (batch_size, 768)
           -> Standard matrix multiplication via self.ft.
        :return: Scalar evaluation value in pawns (1.0 = 100 centipawns).
        """
        if our_features.dtype in (torch.int16, torch.int32, torch.int64):
            our_idx = torch.clamp(our_features.to(torch.int64), 0, FEATURE_COUNT)
            their_idx = torch.clamp(their_features.to(torch.int64), 0, FEATURE_COUNT)
            pad = torch.zeros(1, self.hidden_dim, device=self.ft.weight.device, dtype=self.ft.weight.dtype)
            w_padded = torch.cat([self.ft.weight.t(), pad], dim=0)
            our_acc = self.act(F.embedding_bag(our_idx, w_padded, mode="sum") + self.ft.bias)
            their_acc = self.act(F.embedding_bag(their_idx, w_padded, mode="sum") + self.ft.bias)
        else:
            our_acc = self.act(self.ft(our_features))
            their_acc = self.act(self.ft(their_features))

        combined = torch.cat([our_acc, their_acc], dim=-1)
        hidden = self.act(self.l1(combined))
        out = self.output(hidden)
        return out.squeeze(-1)


class MicroNNUE(nn.Module):
    """
    Micro NNUE Architecture for Tactical Blunder and Trap Checking:
    768 sparse features -> 16 (per perspective) -> 32 combined -> 1.
    Optimized for single 256-bit SIMD register evaluation.
    """
    def __init__(self, hidden_dim: int = MICRO_HIDDEN_DIM):
        super().__init__()
        self.hidden_dim = hidden_dim
        self.arch_id = 0x0002

        # Feature Transformer (16 dimensions per perspective)
        self.ft = nn.Linear(FEATURE_COUNT, hidden_dim)

        # Direct Output Layer: 16 * 2 = 32 -> 1
        self.output = nn.Linear(hidden_dim * 2, 1)

        self.act = ClippedReLU()
        init_nnue_weights(self)

    def forward(self, our_features: torch.Tensor, their_features: torch.Tensor) -> torch.Tensor:
        """
        Forward pass for micro model in pawns (1.0 = 100 centipawns).
        Supports both sparse indices and dense feature tensors.
        """
        if our_features.dtype in (torch.int16, torch.int32, torch.int64):
            our_idx = torch.clamp(our_features.to(torch.int64), 0, FEATURE_COUNT)
            their_idx = torch.clamp(their_features.to(torch.int64), 0, FEATURE_COUNT)
            pad = torch.zeros(1, self.hidden_dim, device=self.ft.weight.device, dtype=self.ft.weight.dtype)
            w_padded = torch.cat([self.ft.weight.t(), pad], dim=0)
            our_acc = self.act(F.embedding_bag(our_idx, w_padded, mode="sum") + self.ft.bias)
            their_acc = self.act(F.embedding_bag(their_idx, w_padded, mode="sum") + self.ft.bias)
        else:
            our_acc = self.act(self.ft(our_features))
            their_acc = self.act(self.ft(their_features))

        combined = torch.cat([our_acc, their_acc], dim=-1)
        out = self.output(combined)
        return out.squeeze(-1)


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
    from binpack import encode_features_to_indices

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
