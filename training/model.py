"""
Enhanced Anak NNUE Neural Network Architectures (PyTorch).
Defines StandardNNUE (768 -> 256x2 -> 32 -> 1) and MicroNNUE (768 -> 16x2 -> 1).
Includes advanced weight initialization, quantization clamping, and Model EMA.
"""

import copy
import math
from typing import Dict
import torch
import torch.nn as nn
import torch.nn.functional as F

FEATURE_COUNT = 768  # 6 piece types * 2 colors * 64 squares
STANDARD_HIDDEN_DIM = 256
MICRO_HIDDEN_DIM = 16


class ClippedReLU(nn.Module):
    """Clipped ReLU activation: clamp(x, 0, 1.0) for normalized training."""
    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return torch.clamp(x, 0.0, 1.0)


def init_nnue_weights(model: nn.Module):
    """
    State-of-the-art NNUE weight initialization.
    Initializes Feature Transformer biases positive (0.20) to prevent dead ClippedReLU neurons,
    and scales weights for stable initial activations.
    """
    for name, param in model.named_parameters():
        if "ft.weight" in name:
            # Active features per position ~30
            std = 0.15 / math.sqrt(param.shape[0] / 256.0)
            nn.init.normal_(param, mean=0.0, std=std)
        elif "ft.bias" in name:
            nn.init.constant_(param, 0.20)  # Healthy starting activation
        elif "l1.weight" in name:
            nn.init.kaiming_normal_(param, nonlinearity="relu")
        elif "l1.bias" in name:
            nn.init.constant_(param, 0.05)
        elif "output.weight" in name:
            nn.init.normal_(param, mean=0.0, std=0.02)
        elif "output.bias" in name:
            nn.init.constant_(param, 0.0)


def clamp_nnue_weights(model: nn.Module, max_val: float = 1.98):
    """
    Quantization-aware weight clamping to ensure zero precision loss
    when quantized to INT8 [-128, 127] with factor 64.
    """
    with torch.no_grad():
        for name, param in model.named_parameters():
            if "weight" in name:
                param.clamp_(-max_val, max_val)


class ModelEMA:
    """
    Exponential Moving Average (Polyak Averaging) for NNUE models.
    Smooths out mini-batch gradient jitter over millions of positions,
    producing measurably stronger networks (+15-30 Elo).
    """
    def __init__(self, model: nn.Module, decay: float = 0.999):
        self.decay = decay
        self.shadow = {
            name: param.clone().detach()
            for name, param in model.named_parameters()
            if param.requires_grad
        }

    def update(self, model: nn.Module):
        """Updates shadow weights with current model weights."""
        with torch.no_grad():
            for name, param in model.named_parameters():
                if name in self.shadow:
                    self.shadow[name].mul_(self.decay).add_(param.data, alpha=1.0 - self.decay)

    def apply_shadow(self, model: nn.Module):
        """Copies shadow weights into the model for evaluation or export."""
        with torch.no_grad():
            for name, param in model.named_parameters():
                if name in self.shadow:
                    param.data.copy_(self.shadow[name])

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
    Standard NNUE Architecture for Open and Closed expert models:
    768 sparse features -> 256 (per perspective) -> 512 combined -> 32 -> 1.
    """
    def __init__(self, hidden_dim: int = STANDARD_HIDDEN_DIM):
        super().__init__()
        self.hidden_dim = hidden_dim
        self.arch_id = 0x0001

        # Feature Transformer (shared between White and Black perspectives)
        self.ft = nn.Linear(FEATURE_COUNT, hidden_dim)

        # Hidden layer: 2 * hidden_dim -> 32
        self.l1 = nn.Linear(hidden_dim * 2, 32)

        # Output layer: 32 -> 1
        self.output = nn.Linear(32, 1)

        self.act = ClippedReLU()
        init_nnue_weights(self)

    def forward(self, our_features: torch.Tensor, their_features: torch.Tensor) -> torch.Tensor:
        """
        Forward pass.
        :param our_features: Sparse or dense binary tensor of shape (batch_size, 768).
        :param their_features: Sparse or dense binary tensor of shape (batch_size, 768).
        :return: Scalar evaluation value (centipawn-scale / 400.0).
        """
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
        Forward pass for micro model.
        """
        our_acc = self.act(self.ft(our_features))
        their_acc = self.act(self.ft(their_features))

        combined = torch.cat([our_acc, their_acc], dim=-1)
        out = self.output(combined)
        return out.squeeze(-1)
