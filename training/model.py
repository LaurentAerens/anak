"""
Anak NNUE Neural Network Architectures (PyTorch).
Defines StandardNNUE (768 -> 256x2 -> 32 -> 1) and MicroNNUE (768 -> 16x2 -> 1).
"""

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

    def forward(self, our_features: torch.Tensor, their_features: torch.Tensor) -> torch.Tensor:
        """
        Forward pass for micro model.
        """
        our_acc = self.act(self.ft(our_features))
        their_acc = self.act(self.ft(their_features))

        combined = torch.cat([our_acc, their_acc], dim=-1)
        out = self.output(combined)
        return out.squeeze(-1)
