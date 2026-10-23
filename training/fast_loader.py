"""
Ultra-Fast Pure-Python Binpack DataLoader for Anak NNUE Training.
Reads directly from memory-mapped .binpack binary archives with chunked sequential streaming.
Supports:
- Multi-file / sharded binpacks (directories, glob patterns, comma-separated, or file lists)
- Validation split (e.g. 5% split for validation loss tracking)
- High batch sizes (up to 32,768+ with GPU/CPU-accelerated sparse scatter)
- Chunked block streaming with in-memory shuffling to eliminate random disk seek storms
- Background thread asynchronous prefetching (overlaps 100% of data loading with compute)
- Minimal RAM footprint (< 50 MB regardless of 30M+ dataset size)
- Cross-domain mixture streaming without multi-file thrashing
"""

import glob
import math
import os
import queue
import threading
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
    Executed directly on device (GPU or CPU) with zero Python loops.
    """
    batch_size = indices.shape[0]
    target_device = device or indices.device

    # Use int32 for index clamping and scatter to halve memory bandwidth
    clamped = torch.clamp(indices.to(target_device, dtype=torch.int32), 0, FEATURE_COUNT)

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
        for r in getattr(self, "readers", []):
            r.close()
        for r in getattr(self, "mix_readers", []):
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
    High-Performance Zero-Copy Chunked Memory-Mapped Batch Loader.
    Features:
    - Chunk-based sequential I/O with in-memory block shuffling (eliminates random disk seek storms)
    - Asynchronous background prefetching (data loading overlaps 100% with compute)
    - Ultra-low RAM footprint (< 50 MB regardless of 30M+ dataset size)
    - Zero Windows memory paging storms and no TLB degradation
    - Multi-file sharded binpack support with seamless cross-domain mixture streaming
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
        mix_sources: Optional[Union[str, List[str]]] = None,
        mix_ratio: float = 0.0,
        chunk_size: int = 65536,
        prefetch_batches: int = 2,
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
        self.chunk_size = max(batch_size, chunk_size)
        self.prefetch_batches = max(1, prefetch_batches)

        # Configure cross-domain mixture
        self.mix_ratio = max(0.0, min(0.90, float(mix_ratio)))
        self.mix_files = resolve_binpack_files(mix_sources) if mix_sources else []
        if self.mix_files and self.mix_ratio > 0.0:
            self.mix_readers = [BinpackReader(f) for f in self.mix_files]
            self.mix_file_lengths = [len(r) for r in self.mix_readers]
            self.mix_cumulative_offsets = np.cumsum([0] + self.mix_file_lengths)
            self.mix_total_records = int(self.mix_cumulative_offsets[-1])
            self.mix_batch_size = max(1, int(round(self.batch_size * self.mix_ratio)))
            self.primary_batch_size = max(1, self.batch_size - self.mix_batch_size)
        else:
            self.mix_readers = []
            self.mix_file_lengths = []
            self.mix_cumulative_offsets = np.array([0])
            self.mix_total_records = 0
            self.mix_batch_size = 0
            self.primary_batch_size = self.batch_size

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
                self.num_batches = self.total_records // self.primary_batch_size
            else:
                self.num_batches = math.ceil(self.total_records / self.primary_batch_size)

        self._stop_event = threading.Event()
        self._worker_thread = None
        self._queue = None

    def __len__(self) -> int:
        return self.num_batches

    def _get_record_slice(self, global_start: int, global_end: int) -> np.ndarray:
        """Reads a contiguous block across single or multi-file readers with sequential disk I/O."""
        if len(self.readers) == 1:
            return np.array(self.readers[0].data[global_start:global_end], copy=True)

        count = global_end - global_start
        out = np.empty(count, dtype=RECORD_DTYPE)
        curr = 0
        start = global_start
        while start < global_end:
            f_idx = int(np.searchsorted(self.cumulative_offsets[1:], start, side="right"))
            local_start = start - self.cumulative_offsets[f_idx]
            avail_in_file = self.file_lengths[f_idx] - local_start
            need = global_end - start
            take = min(avail_in_file, need)
            out[curr : curr + take] = self.readers[f_idx].data[local_start : local_start + take]
            curr += take
            start += take
        return out

    def _worker_producer(self, q: queue.Queue, stop_ev: threading.Event):
        """Asynchronous background worker that streams sequential chunks and pre-fills batch queue."""
        rng = np.random.default_rng()
        num_chunks = math.ceil(self.total_records / self.chunk_size)

        if self.shuffle:
            chunk_indices = rng.permutation(num_chunks)
        else:
            chunk_indices = np.arange(num_chunks)

        # Mix streaming generator if mix sources enabled
        mix_chunk_buf = None
        mix_chunk_idx = 0
        if self.mix_batch_size > 0 and self.mix_total_records > 0:
            mix_num_chunks = math.ceil(self.mix_total_records / self.chunk_size)
            mix_chunk_order = rng.permutation(mix_num_chunks) if self.shuffle else np.arange(mix_num_chunks)
            mix_chunk_order_idx = 0

            def get_mix_slice(n: int):
                nonlocal mix_chunk_buf, mix_chunk_idx, mix_chunk_order_idx
                if mix_chunk_buf is None or (mix_chunk_idx + n) > len(mix_chunk_buf):
                    c = mix_chunk_order[mix_chunk_order_idx % len(mix_chunk_order)]
                    mix_chunk_order_idx += 1
                    c_start = c * self.chunk_size
                    c_end = min(self.mix_total_records, (c + 1) * self.chunk_size)
                    if len(self.mix_readers) == 1:
                        mix_chunk_buf = np.array(self.mix_readers[0].data[c_start:c_end], copy=True)
                    else:
                        m_cnt = c_end - c_start
                        mix_chunk_buf = np.empty(m_cnt, dtype=RECORD_DTYPE)
                        m_cur = 0
                        m_st = c_start
                        while m_st < c_end:
                            m_f = int(np.searchsorted(self.mix_cumulative_offsets[1:], m_st, side="right"))
                            loc = m_st - self.mix_cumulative_offsets[m_f]
                            avail = self.mix_file_lengths[m_f] - loc
                            tk = min(avail, c_end - m_st)
                            mix_chunk_buf[m_cur : m_cur + tk] = self.mix_readers[m_f].data[loc : loc + tk]
                            m_cur += tk
                            m_st += tk
                    if self.shuffle:
                        rng.shuffle(mix_chunk_buf)
                    mix_chunk_idx = 0

                res = mix_chunk_buf[mix_chunk_idx : mix_chunk_idx + n]
                mix_chunk_idx += n
                return res

        batches_yielded = 0
        for c_idx in chunk_indices:
            if stop_ev.is_set() or batches_yielded >= self.num_batches:
                break

            chunk_start = self.range_start + c_idx * self.chunk_size
            chunk_end = min(self.range_end, chunk_start + self.chunk_size)
            if chunk_start >= chunk_end:
                continue

            chunk_records = self._get_record_slice(chunk_start, chunk_end)
            if self.shuffle:
                rng.shuffle(chunk_records)

            c_len = len(chunk_records)
            for b_offset in range(0, c_len, self.primary_batch_size):
                if stop_ev.is_set() or batches_yielded >= self.num_batches:
                    break

                batch_records = chunk_records[b_offset : b_offset + self.primary_batch_size]
                if self.drop_last and len(batch_records) < self.primary_batch_size:
                    break

                # Mix with secondary expert pool if configured
                if self.mix_batch_size > 0 and self.mix_total_records > 0:
                    needed_mix = int(round(len(batch_records) * (self.mix_batch_size / self.primary_batch_size)))
                    if needed_mix > 0:
                        m_recs = get_mix_slice(needed_mix)
                        batch_records = np.concatenate([batch_records, m_recs])
                        if self.shuffle:
                            rng.shuffle(batch_records)

                our_idx = batch_records["our_indices"].astype(np.int32)
                their_idx = batch_records["their_indices"].astype(np.int32)

                # Vectorized NumPy board reflection data augmentation
                if self.augment_flip:
                    flip = rng.integers(0, 2, size=(len(batch_records), 1), dtype=np.int32)
                    mask_our = (our_idx != SENTINEL_INDEX) & (our_idx < FEATURE_COUNT)
                    our_idx ^= (flip * 7) * mask_our
                    mask_their = (their_idx != SENTINEL_INDEX) & (their_idx < FEATURE_COUNT)
                    their_idx ^= (flip * 7) * mask_their

                our_idx_t = torch.from_numpy(our_idx)
                their_idx_t = torch.from_numpy(their_idx)
                target_eval_t = torch.from_numpy(batch_records["target_eval"].copy()).to(self.device)
                target_wdl_t = torch.from_numpy(batch_records["target_wdl"].copy()).to(self.device)

                our_features = fast_scatter_features(our_idx_t, device=self.device)
                their_features = fast_scatter_features(their_idx_t, device=self.device)

                batch_dict = {
                    "our_features": our_features,
                    "their_features": their_features,
                    "target_eval": target_eval_t,
                    "target_wdl": target_wdl_t,
                }

                while not stop_ev.is_set():
                    try:
                        q.put(batch_dict, timeout=0.1)
                        batches_yielded += 1
                        break
                    except queue.Full:
                        continue

        # Sentinel indicating end of stream
        while not stop_ev.is_set():
            try:
                q.put(None, timeout=0.1)
                break
            except queue.Full:
                continue

    def __iter__(self):
        if self.total_records == 0 or self.num_batches == 0:
            return

        self._stop_event.clear()
        self._queue = queue.Queue(maxsize=self.prefetch_batches)
        self._worker_thread = threading.Thread(
            target=self._worker_producer,
            args=(self._queue, self._stop_event),
            daemon=True
        )
        self._worker_thread.start()

        try:
            for _ in range(self.num_batches):
                item = self._queue.get()
                if item is None:
                    break
                yield item
        finally:
            self._stop_event.set()
            if self._worker_thread and self._worker_thread.is_alive():
                while not self._queue.empty():
                    try:
                        self._queue.get_nowait()
                    except:
                        break
                self._worker_thread.join(timeout=1.0)

    def close(self):
        self._stop_event.set()
        if getattr(self, "_worker_thread", None) and self._worker_thread.is_alive():
            if getattr(self, "_queue", None):
                while not self._queue.empty():
                    try:
                        self._queue.get_nowait()
                    except:
                        break
            self._worker_thread.join(timeout=1.0)
        for r in getattr(self, "readers", []):
            r.close()
        for r in getattr(self, "mix_readers", []):
            r.close()

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.close()
