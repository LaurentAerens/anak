"""
Loss functions for Anak NNUE training.
Handles TCEC draw rates via WDL + Huber Eval blending and asymmetric blunder penalties for MicroNNUE.
"""

import torch
import torch.nn as nn
import torch.nn.functional as F


class TcecBlendedLoss(nn.Module):
    """
    Blended loss function balancing true game outcomes (WDL) with deep search evaluations.
    Essential for TCEC datasets to prevent draw-bias resulting from high draw rates.
    """
    def __init__(self, lambda_wdl: float = 0.40):
        super().__init__()
        self.lambda_wdl = lambda_wdl
        self.wdl_loss = nn.BCEWithLogitsLoss()
        self.eval_loss = nn.SmoothL1Loss()

    def forward(self, pred: torch.Tensor, target_wdl: torch.Tensor, target_eval: torch.Tensor) -> torch.Tensor:
        """
        :param pred: Raw model logit / score.
        :param target_wdl: Ground truth game outcome in [0.0, 1.0].
        :param target_eval: Engine evaluation in pawns (e.g. +1.25).
        """
        # Map raw engine eval to tanh [-1.0, 1.0] scale
        scaled_eval = torch.tanh(target_eval / 4.0)

        # 1. WDL Cross-Entropy Loss
        l_wdl = self.wdl_loss(pred, target_wdl)

        # 2. Evaluation Huber Loss
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
