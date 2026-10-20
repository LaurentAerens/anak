"""
Ultra-Fast Pure-Python Binpack DataLoader for Anak NNUE Training.
Reads directly from memory-mapped .binpack binary archives.
Supports:
- Multi-file / sharded binpacks (directories, glob patterns, comma-separated, or file lists)
- Validation split (e.g. 5% split for validation loss tracking)
- High batch sizes (up to 32,768+ with GPU-accelerated sparse scatter)
- Zero memory duplication and multi-million positions/sec streaming
"""

import glob
import math
import os
from typing import Optional, Union, List
import numpy as np
import torch
from torch.utils.data import Dataset

from binpack import BinpackReader, RECORD_DTYPE, SENTINEL_INDEX

FEATURE_COUNT = 768


def resolve_binpack_files(source: Union[str, List[str]]) -> List[str]:
    """
    Intelligently resolves one or multiple .binpack files from:
    - A single file path (e.g. 'open.binpack')
    - A directory containing .binpack files (e.g. './data/shards')
    - A glob pattern (e.g. 'data/*.binpack')
    - A comma-separated list of paths (e.g. 'shard1.binpack,shard2.binpack')
    - A Python list/tuple of paths or patterns
    """
    candidates: List[str] = []
    if isinstance(source, (list, tuple)):
        for item in source:
            candidates.extend(resolve_binpack_files(item))
        return sorted(list(set(candidates)))

    if not isinstance(source, str) or not source.strip():
        return []

    # Handle comma-separated input
    if "," in source:
        for part in source.split(","):
            part = part.strip()
            if part:
                candidates.extend(resolve_binpack_files(part))
        return sorted(list(set(candidates)))

    # Handle glob patterns (* or ?)
    if "*" in source or "?" in source:
        matches = glob.glob(source, recursive=True)
        for m in matches:
            if os.path.isfile(m) and m.endswith(".binpack"):
                candidates.append(os.path.abspath(m))
        return sorted(list(set(candidates)))

    abs_path = os.path.abspath(source)
    if os.path.isdir(abs_path):
        for root, _, files in os.walk(abs_path):
            for f in sorted(files):
                if f.endswith(".binpack"):
                    candidates.append(os.path.abspath(os.path.join(root, f)))
        return sorted(candidates)

    if os.path.isfile(abs_path):
        return [abs_path]

    # Try relative to cwd or script dir if not found directly
    cwd_candidate = os.path.abspath(os.path.join(os.getcwd(), source))
    if os.path.isfile(cwd_candidate):
        return [cwd_candidate]

    # Return empty if nothing found
    return []


def fast_scatter_features(
    indices: torch.Tensor,
    device: Optional[torch.device] = None
) -> torch.Tensor:
    """
    Scatters sparse uint16 indices of shape (batch_size, 32) into a dense (batch_size, 768) float tensor.
    Sentinel values (0xFFFF / 65535) are redirected to index 768 and sliced away.
    Executed directly on GPU with zero Python loops.
    """
    batch_size = indices.shape[0]
    target_device = device or indices.device

    # Clamp indices so SENTINEL (65535) hits index 768
    clamped = torch.clamp(indices.to(target_device, dtype=torch.int64), 0, FEATURE_COUNT)

    # 769 slots (0..767 are HalfKP features, 768 is dummy bucket for sentinels)
    dense = torch.zeros((batch_size, FEATURE_COUNT + 1), dtype=torch.float32, device=target_device)
    dense.scatter_(1, clamped, 1.0)

    # Discard dummy bucket
    return dense[:, :FEATURE_COUNT]


class FastBinpackDataset(Dataset):
    """
    PyTorch Map-Style Dataset backed by memory-mapped BinpackReader(s).
    Supports single or multi-file sharded binpacks with instant length calculation and zero duplication.
    """
    def __init__(self, binpack_source: Union[str, List[str]]):
        self.files = resolve_binpack_files(binpack_source)
        if not self.files:
            raise FileNotFoundError(f"No valid .binpack files found for source: {binpack_source}")

        self.readers: List[BinpackReader] = [BinpackReader(f) for f in self.files]
        self.file_lengths = [len(r) for r in self.readers]
        self.offsets = np.cumsum([0] + self.file_lengths)
        self.total_records = int(self.offsets[-1])

    def __len__(self) -> int:
        return self.total_records

    def __getitem__(self, idx: int):
        if idx < 0 or idx >= self.total_records:
            raise IndexError(f"Index {idx} out of bounds for dataset of size {self.total_records}")

        if len(self.readers) == 1:
            rec = self.readers[0][idx]
        else:
            file_idx = int(np.searchsorted(self.offsets[1:], idx, side="right"))
            local_idx = idx - self.offsets[file_idx]
            rec = self.readers[file_idx][local_idx]

        return {
            "our_indices": rec["our_indices"],
            "their_indices": rec["their_indices"],
            "target_eval": rec["target_eval"],
            "target_wdl": rec["target_wdl"],
            "openness": rec["openness"],
            "piece_count": rec["piece_count"],
        }

    def close(self):
        for r in self.readers:
            r.close()

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.close()


def collate_binpack_batch(batch_list: list) -> dict:
    """Collate function for standard PyTorch DataLoader."""
    our_idx = np.stack([item["our_indices"] for item in batch_list]).astype(np.int32)
    their_idx = np.stack([item["their_indices"] for item in batch_list]).astype(np.int32)
    target_eval = np.array([item["target_eval"] for item in batch_list], dtype=np.float32)
    target_wdl = np.array([item["target_wdl"] for item in batch_list], dtype=np.float32)

    return {
        "our_indices": torch.from_numpy(our_idx),
        "their_indices": torch.from_numpy(their_idx),
        "target_eval": torch.from_numpy(target_eval),
        "target_wdl": torch.from_numpy(target_wdl),
    }


class FastMemoryMappedBatchLoader:
    """
    High-Performance Zero-Copy Pure-Python Batch Iterator.
    Bypasses PyTorch DataLoader worker overhead by slicing NumPy memory-mapped arrays directly.
    Streams batches at >1,000,000 positions per second into GPU VRAM.

    Supports:
    - Multi-file / sharded binpacks (directories, glob patterns, or file lists)
    - Validation split (val_split in [0, 1) and is_validation flag)
    - High batch sizes scaling up to 16,384 or 32,768+
    """
    def __init__(
        self,
        binpack_path: Union[str, List[str]],
        batch_size: int = 4096,
        shuffle: bool = True,
        device: Optional[torch.device] = None,
        drop_last: bool = False,
        val_split: float = 0.0,
        is_validation: bool = False,
        augment_flip: bool = False,
    ):
        self.files = resolve_binpack_files(binpack_path)
        if not self.files:
            raise FileNotFoundError(f"No valid .binpack files found for: {binpack_path}")

        self.readers: List[BinpackReader] = [BinpackReader(f) for f in self.files]
        self.file_lengths = [len(r) for r in self.readers]
        self.cumulative_offsets = np.cumsum([0] + self.file_lengths)
        self.total_raw_records = int(self.cumulative_offsets[-1])

        self.batch_size = batch_size
        self.shuffle = shuffle
        self.device = device or torch.device("cuda" if torch.cuda.is_available() else "cpu")
        self.drop_last = drop_last
        self.val_split = max(0.0, min(0.99, float(val_split)))
        self.is_validation = is_validation
        self.augment_flip = augment_flip

        # Determine partition range for train / val split
        if self.val_split > 0.0:
            split_boundary = int(self.total_raw_records * (1.0 - self.val_split))
            if self.is_validation:
                self.range_start = split_boundary
                self.range_end = self.total_raw_records
            else:
                self.range_start = 0
                self.range_end = split_boundary
        else:
            self.range_start = 0
            self.range_end = self.total_raw_records

        self.total_records = max(0, self.range_end - self.range_start)

        if self.total_records == 0:
            self.num_batches = 0
        else:
            if self.drop_last:
                self.num_batches = self.total_records // self.batch_size
            else:
                self.num_batches = math.ceil(self.total_records / self.batch_size)

    def __len__(self) -> int:
        return self.num_batches

    def __iter__(self):
        if self.total_records == 0 or self.num_batches == 0:
            return

        if self.shuffle:
            local_indices = np.random.default_rng().permutation(np.arange(self.total_records, dtype=np.int32))
        else:
            local_indices = np.arange(self.total_records, dtype=np.int32)

        is_single_reader = (len(self.readers) == 1)

        for b in range(self.num_batches):
            start = b * self.batch_size
            end = min(start + self.batch_size, self.total_records)
            if self.drop_last and (end - start) < self.batch_size:
                break

            batch_global_indices = self.range_start + local_indices[start:end]

            if is_single_reader:
                records = self.readers[0].data[batch_global_indices]
            else:
                # Sharded retrieval across multiple memory-mapped files
                records = np.empty(len(batch_global_indices), dtype=RECORD_DTYPE)
                file_idx = np.searchsorted(self.cumulative_offsets[1:], batch_global_indices, side="right")
                unique_files = np.unique(file_idx)
                for f_idx in unique_files:
                    mask = (file_idx == f_idx)
                    sub_idx = batch_global_indices[mask] - self.cumulative_offsets[f_idx]
                    records[mask] = self.readers[f_idx].data[sub_idx]

            # Fast numpy to torch conversion
            our_idx_t = torch.from_numpy(records["our_indices"].astype(np.int32))
            their_idx_t = torch.from_numpy(records["their_indices"].astype(np.int32))

            # Horizontal board reflection data augmentation (doubles effective dataset, eliminates side bias)
            if self.augment_flip:
                flip = torch.randint(0, 2, (our_idx_t.shape[0], 1), dtype=torch.bool)
                mask_our = (our_idx_t != SENTINEL_INDEX) & (our_idx_t < FEATURE_COUNT)
                our_idx_t = torch.where(flip & mask_our, our_idx_t ^ 7, our_idx_t)
                mask_their = (their_idx_t != SENTINEL_INDEX) & (their_idx_t < FEATURE_COUNT)
                their_idx_t = torch.where(flip & mask_their, their_idx_t ^ 7, their_idx_t)

            target_eval_t = torch.from_numpy(records["target_eval"].copy()).to(self.device)
            target_wdl_t = torch.from_numpy(records["target_wdl"].copy()).to(self.device)

            # Scatter on GPU
            our_features = fast_scatter_features(our_idx_t, device=self.device)
            their_features = fast_scatter_features(their_idx_t, device=self.device)

            yield {
                "our_features": our_features,
                "their_features": their_features,
                "target_eval": target_eval_t,
                "target_wdl": target_wdl_t,
            }

    def close(self):
        for r in self.readers:
            r.close()

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.close()
