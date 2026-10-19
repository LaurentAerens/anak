"""
Loss functions for Anak NNUE training.
Features:
- ModernNNUELoss: Stockfish-style win-probability BCE loss blending search evaluations and WDL.
- TcecBlendedLoss: Smooth-L1 Huber + BCE WDL loss for backward compatibility.
- BlunderLoss: Asymmetric 5x gradient penalty watchdog loss for Micro-NNUE.
"""

import torch
import torch.nn as nn
import torch.nn.functional as F


class ModernNNUELoss(nn.Module):
    """
    Modern Stockfish-style NNUE loss function.
    Converts engine evaluations to expected win probability via sigmoid:
      P_eval = sigmoid(target_eval / eval_scale)
      target_prob = lambda_wdl * target_wdl + (1 - lambda_wdl) * P_eval
    Computes Binary Cross Entropy with Logits against network prediction:
      loss = BCEWithLogits(pred / eval_scale, target_prob)
    This yields smooth, un-saturating gradients and naturally mitigates draw bias.
    """
    def __init__(self, lambda_wdl: float = 0.40, eval_scale: float = 2.50):
        super().__init__()
        self.lambda_wdl = lambda_wdl
        self.eval_scale = eval_scale

    def forward(self, pred: torch.Tensor, target_wdl: torch.Tensor, target_eval: torch.Tensor) -> torch.Tensor:
        # Sigmoid of search evaluation in pawns
        p_eval = torch.sigmoid(target_eval / self.eval_scale)

        # Blended ground truth probability
        target_prob = (self.lambda_wdl * target_wdl) + ((1.0 - self.lambda_wdl) * p_eval)

        # Logit scaling
        logits = pred / self.eval_scale
        return F.binary_cross_entropy_with_logits(logits, target_prob)


class TcecBlendedLoss(nn.Module):
    """
    Blended loss function balancing true game outcomes (WDL) with deep search evaluations.
    """
    def __init__(self, lambda_wdl: float = 0.40):
        super().__init__()
        self.lambda_wdl = lambda_wdl
        self.wdl_loss = nn.BCEWithLogitsLoss()
        self.eval_loss = nn.SmoothL1Loss()

    def forward(self, pred: torch.Tensor, target_wdl: torch.Tensor, target_eval: torch.Tensor) -> torch.Tensor:
        scaled_eval = torch.tanh(target_eval / 4.0)
        l_wdl = self.wdl_loss(pred, target_wdl)
        pred_scaled = torch.tanh(pred / 4.0)
        l_eval = self.eval_loss(pred_scaled, scaled_eval)
        return (self.lambda_wdl * l_wdl) + ((1.0 - self.lambda_wdl) * l_eval)


class BlunderLoss(nn.Module):
    """
    Asymmetric loss function specifically tailored for the Micro-NNUE watchdog.
    Penalizes false negatives (failing to recognize a tactical blunder or trap)
    with a 5x higher gradient weight.
    """
    def __init__(self, blunder_weight: float = 5.0):
        super().__init__()
        self.blunder_weight = blunder_weight
        self.base_loss = nn.SmoothL1Loss(reduction="none")

    def forward(self, pred: torch.Tensor, target_eval: torch.Tensor) -> torch.Tensor:
        pred_scaled = torch.tanh(pred / 4.0)
        target_scaled = torch.tanh(target_eval / 4.0)

        element_loss = self.base_loss(pred_scaled, target_scaled)

        # Heavy penalty mask: position is a severe blunder (target <= -0.50), but model predicted safe (pred >= 0)
        blunder_mask = (target_scaled <= -0.50) & (pred_scaled >= 0.0)
        weights = torch.where(blunder_mask, self.blunder_weight, 1.0)

        return (element_loss * weights).mean()
