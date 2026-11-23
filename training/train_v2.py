"""
Clean, High-Speed Trainer & Exporter for Anak ArchV2 (0x0004) NNUE:
  (KingBuckets x 768 -> HL) x 2 [INT16 QA=255, True Horizontal Mirroring, Factorized FT]
  -> OutputBuckets x (2*HL -> 1) [INT16 QB=64, SCReLU, 400 cp scale]

Fixes all 7 root-cause defects identified in the NNUE audit:
  1. True Horizontal Mirroring (sq ^ 7 when friendly king is on files E-H).
  2. INT16 Feature Transformer (QA=255) with zero SCReLU dead-zone and zero intermediate truncation.
  3. Clean WDL MSE loss (exact raw game WDL recovery + calibrated 400 cp scale, no raw-cp Huber tug-of-war).
  4. No perspective antisymmetry projection and no out_bias tempo clamping.
  5. 8 smooth material-phase output buckets ((pieceCount - 2) / 4) instead of discontinuous MoE rules.
  6. Zero weight decay on sparse FT embeddings + fast nn.EmbeddingBag + Factorized FT folded at export.
  7. Validation evaluated directly on EMA shadow weights + exact C# integer quantization parity check.
"""

import argparse
import math
import os
import struct
import time
from typing import List, Optional, Tuple

import chess
import numpy as np
import torch
import torch.nn as nn
from tqdm import tqdm

from binpack import encode_features_to_indices, SENTINEL_INDEX
from fast_loader import FastMemoryMappedBatchLoader, resolve_binpack_files

MAGIC_HEADER = 0x4B414E41  # "ANAK"
FORMAT_VERSION = 1
ARCH_V2 = 0x0004

RAW_FEATURE_COUNT = 768
KING_BUCKETS = 16
V2_QA = 255
V2_QB = 64
V2_OUTPUT_SCALE = 400


def map_v2_indices(
    raw_indices: torch.Tensor,
    king_buckets: int = KING_BUCKETS,
) -> Tuple[torch.Tensor, torch.Tensor]:
    """
    Maps raw HalfKP indices [0..767] (sentinel 65535) to:
      1. base_indices in [0..767] (sentinel 768) with TRUE horizontal mirroring (sq ^ 7 if king on E-H).
      2. bucketed_indices in [0..king_buckets*768-1] (sentinel king_buckets*768).
    """
    valid = raw_indices < RAW_FEATURE_COUNT
    if king_buckets == 1:
        base = torch.where(valid, raw_indices, RAW_FEATURE_COUNT)
        return base, base

    king_mask = (raw_indices >= 320) & (raw_indices < 384)
    king_sq = (torch.where(king_mask, raw_indices, torch.zeros_like(raw_indices))).sum(dim=-1) - 320
    king_sq = torch.clamp(king_sq, 0, 63)

    f = king_sq & 7
    r = king_sq >> 3
    king_mirror = (f >= 4).unsqueeze(-1)
    folded_file = torch.where(f >= 4, 7 - f, f)
    bucket = ((r >> 1) << 2) | folded_file

    mirrored = torch.where(king_mirror, raw_indices ^ 7, raw_indices)
    base_idx = torch.where(valid, mirrored, RAW_FEATURE_COUNT)
    bucketed_idx = torch.where(
        valid,
        mirrored + (bucket * RAW_FEATURE_COUNT).unsqueeze(-1),
        king_buckets * RAW_FEATURE_COUNT,
    )
    return base_idx, bucketed_idx


class PerspectiveV2NNUE(nn.Module):
    """
    Factorized Wide Perspective NNUE:
      - Shared base FT: (768 -> HL) folded into every king bucket at export (0 runtime cost).
      - Bucketed FT: (king_buckets * 768 -> HL) residual per king bucket.
      - Output: output_buckets x (2 * HL -> 1), SCReLU activation, 400 cp logit scale.
    """

    def __init__(
        self,
        hidden_dim: int = 1024,
        king_buckets: int = KING_BUCKETS,
        output_buckets: int = 8,
        use_factorized_ft: bool = True,
    ):
        super().__init__()
        self.hidden_dim = hidden_dim
        self.king_buckets = king_buckets
        self.output_buckets = output_buckets
        self.use_factorized_ft = use_factorized_ft and (king_buckets > 1)
        self.feature_count = king_buckets * RAW_FEATURE_COUNT

        self.ft_bucket = nn.EmbeddingBag(
            self.feature_count + 1,
            hidden_dim,
            mode="sum",
            padding_idx=self.feature_count,
        )
        if self.use_factorized_ft:
            self.ft_base = nn.EmbeddingBag(
                RAW_FEATURE_COUNT + 1,
                hidden_dim,
                mode="sum",
                padding_idx=RAW_FEATURE_COUNT,
            )
        else:
            self.ft_base = None

        self.ft_bias = nn.Parameter(torch.zeros(hidden_dim))
        self.out_weight = nn.Parameter(torch.empty(output_buckets, hidden_dim * 2))
        self.out_bias = nn.Parameter(torch.zeros(output_buckets))

        self._init_weights()

    def _init_weights(self):
        with torch.no_grad():
            nn.init.normal_(self.ft_bucket.weight, mean=0.0, std=0.02 if self.use_factorized_ft else 0.04)
            self.ft_bucket.weight[self.feature_count].zero_()
            if self.ft_base is not None:
                nn.init.normal_(self.ft_base.weight, mean=0.0, std=0.04)
                self.ft_base.weight[RAW_FEATURE_COUNT].zero_()
            self.ft_bias.zero_()
            nn.init.normal_(self.out_weight, mean=0.0, std=1.0 / math.sqrt(self.hidden_dim * 2))
            self.out_bias.zero_()

    def clamp_weights(self):
        with torch.no_grad():
            self.out_weight.clamp_(-1.98, 1.98)
            self.ft_bucket.weight[self.feature_count].zero_()
            if self.ft_base is not None:
                self.ft_base.weight[RAW_FEATURE_COUNT].zero_()

    def forward(self, our_indices: torch.Tensor, their_indices: torch.Tensor) -> torch.Tensor:
        our_raw = our_indices.to(torch.int64)
        their_raw = their_indices.to(torch.int64)

        our_base, our_b = map_v2_indices(our_raw, self.king_buckets)
        their_base, their_b = map_v2_indices(their_raw, self.king_buckets)

        our_acc = self.ft_bucket(our_b) + self.ft_bias
        their_acc = self.ft_bucket(their_b) + self.ft_bias
        if self.ft_base is not None:
            our_acc = our_acc + self.ft_base(our_base)
            their_acc = their_acc + self.ft_base(their_base)

        our_act = torch.clamp(our_acc, 0.0, 1.0)
        our_act = our_act * our_act
        their_act = torch.clamp(their_acc, 0.0, 1.0)
        their_act = their_act * their_act

        combined = torch.cat([our_act, their_act], dim=-1)  # (B, 2*HL)

        if self.output_buckets <= 1:
            return (combined * self.out_weight[0]).sum(dim=-1) + self.out_bias[0]

        piece_count = (our_raw < RAW_FEATURE_COUNT).sum(dim=-1)
        divisor = (32 + self.output_buckets - 1) // self.output_buckets
        out_bucket = torch.clamp((piece_count - 2) // divisor, 0, self.output_buckets - 1)

        w = self.out_weight[out_bucket]  # (B, 2*HL)
        b = self.out_bias[out_bucket]    # (B,)
        return (combined * w).sum(dim=-1) + b


class WdlMseLoss(nn.Module):
    """
    Standard modern NNUE WDL MSE loss (Bullet / Stockfish style).
    Recovers exact raw game WDL from pre-blended Anak binpacks when recover_raw_wdl=True.
    """

    def __init__(
        self,
        wdl_weight: float = 0.25,
        eval_scale_cp: float = 400.0,
        power: float = 2.0,
        recover_raw_wdl: bool = True,
    ):
        super().__init__()
        self.wdl_weight = wdl_weight
        self.eval_scale_cp = eval_scale_cp
        self.power = power
        self.recover_raw_wdl = recover_raw_wdl

    def forward(self, pred: torch.Tensor, target_wdl: torch.Tensor, target_eval: torch.Tensor) -> torch.Tensor:
        if self.recover_raw_wdl:
            # Invert sf_binpack_to_anak.cpp pre-blending:
            #   binpack_wdl = 0.75 * sigmoid(target_eval / 2.5) + 0.25 * game_wdl
            #   target_eval = sf_score / 270.0
            old_eval_wdl = torch.sigmoid(target_eval / 2.5)
            raw_game_wdl = torch.clamp((target_wdl - 0.75 * old_eval_wdl) * 4.0, 0.0, 1.0)
            target_cp = target_eval * 270.0
        else:
            raw_game_wdl = target_wdl
            target_cp = target_eval * 100.0

        pred_prob = torch.sigmoid(pred)
        eval_prob = torch.sigmoid(target_cp / self.eval_scale_cp)
        target_prob = ((1.0 - self.wdl_weight) * eval_prob) + (self.wdl_weight * raw_game_wdl)

        diff = pred_prob - target_prob
        if self.power == 2.0:
            return torch.mean(diff * diff)
        return torch.mean(torch.abs(diff) ** self.power)


class SimpleEMA:
    def __init__(self, model: nn.Module, decay: float = 0.999):
        self.decay = decay
        self.num_updates = 0
        self.shadow = {k: v.detach().clone() for k, v in model.named_parameters() if v.requires_grad}

    def update(self, model: nn.Module):
        self.num_updates += 1
        d = min(self.decay, (1.0 + self.num_updates) / (10.0 + self.num_updates))
        with torch.no_grad():
            for k, v in model.named_parameters():
                if k in self.shadow:
                    self.shadow[k].mul_(d).add_(v.data, alpha=1.0 - d)

    def swap_in(self, model: nn.Module) -> dict:
        backup = {k: v.detach().clone() for k, v in model.named_parameters() if k in self.shadow}
        with torch.no_grad():
            for k, v in model.named_parameters():
                if k in self.shadow:
                    v.data.copy_(self.shadow[k])
        return backup

    def restore(self, model: nn.Module, backup: dict):
        with torch.no_grad():
            for k, v in model.named_parameters():
                if k in backup:
                    v.data.copy_(backup[k])


def get_king_bucket_py(king_sq: int) -> int:
    f = king_sq & 7
    r = king_sq >> 3
    folded = 7 - f if f >= 4 else f
    return ((r >> 1) << 2) | folded


def simulate_csharp_v2(
    ft_weights: np.ndarray,   # (king_buckets * 768, HL) int16
    ft_biases: np.ndarray,    # (HL,) int16
    out_weights: np.ndarray,  # (output_buckets, 2*HL) int16
    out_biases: np.ndarray,   # (output_buckets,) int32
    board: chess.Board,
    king_buckets: int = KING_BUCKETS,
) -> int:
    our_idx, their_idx, count = encode_features_to_indices(board)
    hl = ft_biases.shape[0]
    output_buckets = out_weights.shape[0]

    def accum(indices):
        acc = ft_biases.astype(np.int32).copy()
        valid = [int(x) for x in indices if x < RAW_FEATURE_COUNT]
        if king_buckets == 1:
            b, flip = 0, 0
        else:
            ksq = next((x - 320 for x in valid if 320 <= x < 384), 4)
            b = get_king_bucket_py(ksq)
            flip = 7 if (ksq & 4) != 0 else 0
        offset = b * RAW_FEATURE_COUNT
        for x in valid:
            acc += ft_weights[offset + (x ^ flip)].astype(np.int32)
        return np.clip(acc, -32768, 32767).astype(np.int16)

    our_acc = accum(our_idx)
    their_acc = accum(their_idx)

    divisor = (32 + output_buckets - 1) // output_buckets
    bucket = max(0, min(output_buckets - 1, (count - 2) // divisor))

    ow = out_weights[bucket]
    vo = np.clip(our_acc, 0, V2_QA).astype(np.int32)
    vt = np.clip(their_acc, 0, V2_QA).astype(np.int32)
    # Match C# (short)(v * w) * v
    po = (vo * ow[:hl].astype(np.int32)).astype(np.int16).astype(np.int64) * vo.astype(np.int64)
    pt = (vt * ow[hl:].astype(np.int32)).astype(np.int16).astype(np.int64) * vt.astype(np.int64)
    total = int(po.sum() + pt.sum())
    # C# integer division truncates toward zero
    scaled = int(total / V2_QA) + int(out_biases[bucket])
    return int((scaled * V2_OUTPUT_SCALE) / (V2_QA * V2_QB))


def export_v2(model: PerspectiveV2NNUE, output_path: str):
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    model.clamp_weights()

    hl = model.hidden_dim
    kb = model.king_buckets
    ob = model.output_buckets

    with torch.no_grad():
        w_bucket = model.ft_bucket.weight[: kb * RAW_FEATURE_COUNT].detach().cpu().float().view(kb, RAW_FEATURE_COUNT, hl)
        if model.ft_base is not None:
            w_base = model.ft_base.weight[:RAW_FEATURE_COUNT].detach().cpu().float().unsqueeze(0)
            w_folded = (w_bucket + w_base).reshape(kb * RAW_FEATURE_COUNT, hl)
        else:
            w_folded = w_bucket.reshape(kb * RAW_FEATURE_COUNT, hl)

        ft_weights_q = np.clip(np.round(w_folded.numpy() * V2_QA), -32768, 32767).astype(np.int16)
        ft_biases_q = np.clip(np.round(model.ft_bias.detach().cpu().float().numpy() * V2_QA), -32768, 32767).astype(np.int16)
        out_weights_q = np.clip(np.round(model.out_weight.detach().cpu().float().numpy() * V2_QB), -127, 127).astype(np.int16)
        out_biases_q = np.round(model.out_bias.detach().cpu().float().numpy() * (V2_QA * V2_QB)).astype(np.int32)

    with open(output_path, "wb") as f:
        header = struct.pack("<IIHHHH", MAGIC_HEADER, FORMAT_VERSION, ARCH_V2, hl, ob, kb & 0xFF)
        f.write(header)
        f.write(ft_weights_q.tobytes())
        f.write(ft_biases_q.tobytes())
        for b in range(ob):
            f.write(out_weights_q[b].tobytes())
            f.write(struct.pack("<i", int(out_biases_q[b])))

    # Parity check on canonical positions
    test_fens = [
        ("StartPos", chess.STARTING_FEN),
        ("+Queen", "rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"),
        ("-Queen", "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNB1KBNR w KQkq - 0 1"),
        ("KRvK", "8/8/8/4k3/8/8/4K3/R7 w - - 0 1"),
        ("MirrorKS", "5rk1/5ppp/8/8/8/8/5PPP/5RK1 w - - 0 1"),
        ("MirrorQS", "1kr5/ppp5/8/8/8/8/PPP5/1KR5 w - - 0 1"),
    ]
    model.eval()
    device = next(model.parameters()).device
    checks = []
    max_diff = 0
    with torch.inference_mode():
        for name, fen in test_fens:
            b = chess.Board(fen)
            oi, ti, _ = encode_features_to_indices(b)
            f_cp = int(round(model(
                torch.from_numpy(oi.astype(np.int32))[None].to(device),
                torch.from_numpy(ti.astype(np.int32))[None].to(device),
            ).item() * V2_OUTPUT_SCALE))
            q_cp = simulate_csharp_v2(ft_weights_q, ft_biases_q, out_weights_q, out_biases_q, b, kb)
            max_diff = max(max_diff, abs(f_cp - q_cp))
            checks.append(f"{name}: {q_cp:+d}cp(f:{f_cp:+d})")

    print(f"--> Exported ArchV2 ({kb}x768 -> {hl}x2 -> {ob}x1) to {output_path} ({os.path.getsize(output_path):,} bytes, max_q_err={max_diff}cp)")
    print("    " + " | ".join(checks))


def main():
    parser = argparse.ArgumentParser(description="Train Anak ArchV2 INT16 Perspective NNUE")
    parser.add_argument("--data", type=str, default="./training/data/preprocessed",
                        help="Binpack file(s) or directory (excludes micro/volatility automatically if directory)")
    parser.add_argument("--hidden-dim", type=int, default=1024)
    parser.add_argument("--king-buckets", type=int, default=16)
    parser.add_argument("--output-buckets", type=int, default=8)
    parser.add_argument("--epochs", type=int, default=12)
    parser.add_argument("--batch-size", type=int, default=16384)
    parser.add_argument("--lr", type=float, default=1.2e-3)
    parser.add_argument("--lr-min", type=float, default=1e-5)
    parser.add_argument("--wdl-weight", type=float, default=0.25)
    parser.add_argument("--val-split", type=float, default=0.02)
    parser.add_argument("--max-samples", type=int, default=None, help="Cap training positions per epoch")
    parser.add_argument("--max-val-batches", type=int, default=64)
    parser.add_argument("--output", type=str, default="./networks/exp_v2/anak.nnue")
    parser.add_argument("--ckpt", type=str, default="./training/checkpoints/v2_best.pt")
    parser.add_argument("--workers", type=int, default=8)
    args = parser.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    is_cuda = device.type == "cuda"
    has_bf16 = is_cuda and torch.cuda.is_bf16_supported()
    amp_dtype = torch.bfloat16 if has_bf16 else torch.float16
    if is_cuda:
        torch.set_float32_matmul_precision("high")

    files = resolve_binpack_files(args.data)
    # Exclude micro.binpack and volatility.binpack (volatility has mangled target_eval!)
    files = [f for f in files if os.path.basename(f).lower() not in ("micro.binpack", "volatility.binpack")]
    if not files:
        raise FileNotFoundError(f"No valid training binpacks found in {args.data}")

    print("=" * 78)
    print(f"Anak ArchV2 Trainer | ({args.king_buckets}x768 -> {args.hidden_dim})x2 -> {args.output_buckets}x1 | INT16 QA=255")
    print(f"Device: {torch.cuda.get_device_name(0) if is_cuda else 'CPU'} | AMP: {amp_dtype if is_cuda else 'fp32'}")
    print(f"Files ({len(files)}): {', '.join(os.path.basename(f) for f in files)}")
    print(f"Batch: {args.batch_size:,} | Epochs: {args.epochs} | LR: {args.lr} -> {args.lr_min} | WDL: {args.wdl_weight}")
    print("=" * 78)

    model = PerspectiveV2NNUE(
        hidden_dim=args.hidden_dim,
        king_buckets=args.king_buckets,
        output_buckets=args.output_buckets,
        use_factorized_ft=True,
    ).to(device)
    criterion = WdlMseLoss(wdl_weight=args.wdl_weight, eval_scale_cp=400.0, power=2.0, recover_raw_wdl=True)
    ema = SimpleEMA(model, decay=0.999)

    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0.0, fused=is_cuda)

    train_loader = FastMemoryMappedBatchLoader(
        files,
        batch_size=args.batch_size,
        shuffle=True,
        device=device,
        val_split=args.val_split,
        is_validation=False,
        augment_flip=False,  # True HM makes horizontal flip redundant for own king!
        chunk_size=131072,
        prefetch_batches=16,
        num_workers=args.workers,
    )
    val_loader = FastMemoryMappedBatchLoader(
        files,
        batch_size=args.batch_size * 2,
        shuffle=False,
        device=device,
        val_split=args.val_split,
        is_validation=True,
        augment_flip=False,
        chunk_size=131072,
        prefetch_batches=4,
        num_workers=2,
    )

    steps_per_epoch = len(train_loader)
    if args.max_samples:
        steps_per_epoch = min(steps_per_epoch, math.ceil(args.max_samples / args.batch_size))
    total_steps = max(1, steps_per_epoch * args.epochs)
    warmup_steps = min(500, total_steps // 10)

    def lr_lambda(step: int) -> float:
        if step < warmup_steps:
            return 0.1 + 0.9 * (step / max(1, warmup_steps))
        progress = (step - warmup_steps) / max(1, total_steps - warmup_steps)
        progress = min(1.0, max(0.0, progress))
        cosine = 0.5 * (1.0 + math.cos(math.pi * progress))
        min_ratio = args.lr_min / args.lr
        return min_ratio + (1.0 - min_ratio) * cosine

    scheduler = torch.optim.lr_scheduler.LambdaLR(optimizer, lr_lambda)
    scaler = torch.amp.GradScaler("cuda", enabled=(is_cuda and amp_dtype == torch.float16))

    best_val = float("inf")
    global_step = 0

    try:
        for epoch in range(1, args.epochs + 1):
            t0 = time.time()
            model.train()
            train_loss_sum = 0.0
            batches = 0
            seen = 0

            pbar = tqdm(train_loader, total=steps_per_epoch, desc=f"Epoch {epoch}/{args.epochs}")
            for batch in pbar:
                our_in = batch["our_indices"].to(device, non_blocking=is_cuda)
                their_in = batch["their_indices"].to(device, non_blocking=is_cuda)
                t_wdl = batch["target_wdl"].to(device, non_blocking=is_cuda)
                t_eval = batch["target_eval"].to(device, non_blocking=is_cuda)

                optimizer.zero_grad(set_to_none=True)
                with torch.amp.autocast("cuda" if is_cuda else "cpu", enabled=is_cuda, dtype=amp_dtype):
                    pred = model(our_in, their_in)
                    loss = criterion(pred, t_wdl, t_eval)

                if scaler.is_enabled():
                    scaler.scale(loss).backward()
                    scaler.unscale_(optimizer)
                    torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
                    scaler.step(optimizer)
                    scaler.update()
                else:
                    loss.backward()
                    torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
                    optimizer.step()

                scheduler.step()
                model.clamp_weights()
                ema.update(model)

                global_step += 1
                batches += 1
                seen += our_in.shape[0]
                train_loss_sum += loss.item()

                if batches % 50 == 0 or batches == steps_per_epoch:
                    dt = max(1e-3, time.time() - t0)
                    pbar.set_postfix(loss=f"{train_loss_sum / batches:.5f}", pos_s=f"{seen / dt:,.0f}", lr=f"{scheduler.get_last_lr()[0]:.1e}")

                if args.max_samples and seen >= args.max_samples:
                    break

            # Evaluate validation loss directly on EMA shadow weights!
            backup = ema.swap_in(model)
            model.eval()
            val_loss_sum = 0.0
            val_batches = 0
            with torch.inference_mode():
                for vb in val_loader:
                    v_our = vb["our_indices"].to(device, non_blocking=is_cuda)
                    v_their = vb["their_indices"].to(device, non_blocking=is_cuda)
                    v_wdl = vb["target_wdl"].to(device, non_blocking=is_cuda)
                    v_eval = vb["target_eval"].to(device, non_blocking=is_cuda)
                    with torch.amp.autocast("cuda" if is_cuda else "cpu", enabled=is_cuda, dtype=amp_dtype):
                        v_pred = model(v_our, v_their)
                        v_loss = criterion(v_pred, v_wdl, v_eval)
                    val_loss_sum += v_loss.item()
                    val_batches += 1
                    if args.max_val_batches and val_batches >= args.max_val_batches:
                        break

            avg_train = train_loss_sum / max(1, batches)
            avg_val = val_loss_sum / max(1, val_batches)
            dt = max(1e-3, time.time() - t0)
            print(
                f"Epoch {epoch:2d}/{args.epochs} | Train WDL MSE: {avg_train:.6f} | "
                f"EMA Val WDL MSE: {avg_val:.6f} | {seen / dt:,.0f} pos/s ({dt:.1f}s) | LR: {scheduler.get_last_lr()[0]:.6f}",
                flush=True,
            )

            if avg_val < best_val:
                best_val = avg_val
                os.makedirs(os.path.dirname(os.path.abspath(args.ckpt)), exist_ok=True)
                torch.save({"model_state_dict": model.state_dict(), "val_loss": best_val, "epoch": epoch}, args.ckpt)
                export_v2(model, args.output)

            ema.restore(model, backup)
    finally:
        train_loader.close()
        val_loader.close()

    print(f"Done! Best EMA Val WDL MSE: {best_val:.6f}")


if __name__ == "__main__":
    main()
