"""Offline Stockfish MultiPV=20 move-rank & centipawn-loss telemetry for Zanak's 25-step spine."""

from __future__ import annotations

import argparse
from collections import deque
import csv
import json
from pathlib import Path
import queue
import re
import statistics
import subprocess
import sys
import threading
import time
from typing import Any


MOVE_RE = re.compile(r"^[a-h][1-8][a-h][1-8][qrbn]?$")
DEFAULT_SHOCK_CP = 73
MATE_BASE_CP = 30000
MATE_STEP_CP = 10

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_EPD = Path(__file__).resolve().parent / "telemetry_fens.epd"
DEFAULT_DUMPER_CSPROJ = Path(__file__).resolve().parent / "ZanakSpineDump" / "ZanakSpineDump.csproj"
DEFAULT_DUMPER_DLL = (
    Path(__file__).resolve().parent
    / "ZanakSpineDump"
    / "bin"
    / "Release"
    / "net11.0"
    / "ZanakSpineDump.dll"
)
DEFAULT_STOCKFISH = REPO_ROOT / "tools" / "fastchess" / "bin" / "stockfish.exe"
DEFAULT_OUT_DIR = Path(__file__).resolve().parent


def normalize_uci_score(score_type: str, value: int) -> int:
    """Normalize UCI cp or mate score into a monotonic centipawn scale.

    Mate scores map to +/-(30000 - abs(mate_in) * 10):
      mate +1 -> +29990, mate +3 -> +29970
      mate -1 -> -29990, mate -3 -> -29970, mate 0 -> -30000.
    """
    if score_type == "cp":
        return int(value)
    if score_type == "mate":
        if value > 0:
            return MATE_BASE_CP - value * MATE_STEP_CP
        if value < 0:
            return -MATE_BASE_CP - value * MATE_STEP_CP
        return -MATE_BASE_CP
    raise ValueError(f"Unsupported UCI score type: {score_type}")


def parse_stockfish_info(line: str) -> dict[str, Any] | None:
    """Parse one UCI info line emitted by Stockfish into structured fields."""
    if not line.startswith("info "):
        return None
    tokens = line.split()
    parsed: dict[str, Any] = {}

    for key in ("depth", "seldepth", "multipv", "nodes"):
        if key in tokens:
            idx = tokens.index(key)
            if idx + 1 < len(tokens):
                try:
                    parsed[key] = int(tokens[idx + 1])
                except ValueError:
                    pass

    if "score" in tokens:
        idx = tokens.index("score")
        if idx + 2 < len(tokens) and tokens[idx + 1] in ("cp", "mate"):
            try:
                raw_val = int(tokens[idx + 2])
                parsed["score_type"] = tokens[idx + 1]
                parsed["score_value"] = raw_val
                parsed["score_cp"] = normalize_uci_score(tokens[idx + 1], raw_val)
                if idx + 3 < len(tokens) and tokens[idx + 3] in ("lowerbound", "upperbound"):
                    parsed["bound"] = tokens[idx + 3]
            except ValueError:
                pass

    if "pv" in tokens:
        pv_idx = tokens.index("pv")
        pv_moves = [tok for tok in tokens[pv_idx + 1 :] if MOVE_RE.fullmatch(tok)]
        if pv_moves:
            parsed["pv"] = pv_moves

    return parsed if "score_cp" in parsed and "pv" in parsed else None


def extract_multipv_table(info_lines: list[str], target_depth: int | None = None) -> list[dict[str, Any]]:
    """Collapse UCI info lines into the best/final MultiPV ranking table.

    Prefers exact-bound lines at the target (or maximum observed) depth.
    """
    exact_by_depth: dict[int, dict[int, dict[str, Any]]] = {}
    any_by_depth: dict[int, dict[int, dict[str, Any]]] = {}

    for line in info_lines:
        entry = parse_stockfish_info(line)
        if not entry:
            continue
        depth = int(entry.get("depth", 0))
        mpv = int(entry.get("multipv", 1))
        any_by_depth.setdefault(depth, {})[mpv] = entry
        if "bound" not in entry:
            exact_by_depth.setdefault(depth, {})[mpv] = entry

    if not any_by_depth:
        return []

    source = exact_by_depth if exact_by_depth else any_by_depth
    chosen_depth = target_depth if (target_depth is not None and target_depth in source) else max(source)
    merged: dict[int, dict[str, Any]] = {}
    if chosen_depth in any_by_depth:
        merged.update(any_by_depth[chosen_depth])
    if chosen_depth in exact_by_depth:
        merged.update(exact_by_depth[chosen_depth])
    entries = sorted(merged.values(), key=lambda item: int(item.get("multipv", 1)))
    return entries


def extract_single_move_score(info_lines: list[str], move_uci: str) -> dict[str, Any] | None:
    """Extract final depth score for a targeted `searchmoves <move>` query."""
    entries = extract_multipv_table(info_lines)
    for entry in entries:
        if entry["pv"][0] == move_uci:
            return entry
    return entries[0] if entries else None


class StockfishClient:
    """Persistent UCI wrapper around local Stockfish binary for offline telemetry."""

    def __init__(self, binary_path: Path, threads: int = 1, hash_mb: int = 64, timeout: float = 30.0):
        self.binary_path = Path(binary_path)
        if not self.binary_path.is_file():
            raise FileNotFoundError(f"Stockfish binary not found: {self.binary_path}")
        self.timeout = timeout
        self._lines: queue.Queue[str | None] = queue.Queue()
        self._stderr: deque[str] = deque(maxlen=25)
        self._proc = subprocess.Popen(
            [str(self.binary_path)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        threading.Thread(target=self._read_stdout, daemon=True).start()
        threading.Thread(target=self._read_stderr, daemon=True).start()

        try:
            self._send("uci")
            self._until("uciok")
            self._send(f"setoption name Threads value {threads}")
            self._send(f"setoption name Hash value {hash_mb}")
            self._send("setoption name MultiPV value 20")
            self._send("isready")
            self._until("readyok")
        except BaseException:
            self.close()
            raise

    def _read_stdout(self) -> None:
        assert self._proc.stdout is not None
        try:
            for line in self._proc.stdout:
                self._lines.put(line.rstrip("\r\n"))
        finally:
            self._lines.put(None)

    def _read_stderr(self) -> None:
        assert self._proc.stderr is not None
        for line in self._proc.stderr:
            self._stderr.append(line.rstrip("\r\n"))

    def _send(self, cmd: str) -> None:
        assert self._proc.stdin is not None
        self._proc.stdin.write(cmd + "\n")
        self._proc.stdin.flush()

    def _until(self, marker: str) -> list[str]:
        deadline = time.monotonic() + self.timeout
        collected: list[str] = []
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"Timed out waiting for UCI '{marker}'")
            try:
                line = self._lines.get(timeout=remaining)
            except queue.Empty as exc:
                raise TimeoutError(f"Timed out waiting for UCI '{marker}'") from exc
            if line is None:
                err = "; ".join(self._stderr)
                raise RuntimeError(f"Stockfish exited waiting for '{marker}'" + (f": {err}" if err else ""))
            if line == marker or line.startswith(marker + " "):
                collected.append(line)
                return collected
            collected.append(line)

    def evaluate_position(self, fen: str, zanak_move: str, depth: int = 10, multipv: int = 20) -> dict[str, Any]:
        """Run MultiPV=N at fixed depth, plus targeted searchmoves if zanak_move is outside top N."""
        self._send("ucinewgame")
        self._send(f"setoption name MultiPV value {multipv}")
        self._send("isready")
        self._until("readyok")
        self._send(f"position fen {fen}")
        self._send(f"go depth {depth}")
        lines = self._until("bestmove")

        table = extract_multipv_table(lines, target_depth=depth)
        if not table:
            raise RuntimeError(f"No MultiPV info produced for FEN: {fen}")

        top1 = table[0]
        sf_best_move = top1["pv"][0]
        best_sf_cp = int(top1["score_cp"])
        sf_best_score_type = str(top1["score_type"])
        sf_best_score_value = int(top1["score_value"])
        actual_depth = int(top1.get("depth", depth))

        matched: dict[str, Any] | None = None
        for entry in table:
            if entry["pv"][0] == zanak_move:
                matched = entry
                break

        if matched is not None:
            sf_rank = int(matched.get("multipv", table.index(matched) + 1))
            zanak_sf_cp = int(matched["score_cp"])
            zanak_score_type = str(matched["score_type"])
            zanak_score_value = int(matched["score_value"])
            outside_top_20 = False
        else:
            outside_top_20 = True
            sf_rank = multipv + 1
            self._send("setoption name MultiPV value 1")
            self._send("isready")
            self._until("readyok")
            self._send(f"position fen {fen}")
            self._send(f"go depth {depth} searchmoves {zanak_move}")
            sub_lines = self._until("bestmove")
            sub_entry = extract_single_move_score(sub_lines, zanak_move)
            if sub_entry is None:
                raise RuntimeError(f"No searchmoves score produced for move {zanak_move} in FEN: {fen}")
            zanak_sf_cp = int(sub_entry["score_cp"])
            zanak_score_type = str(sub_entry["score_type"])
            zanak_score_value = int(sub_entry["score_value"])

        cp_loss_raw = best_sf_cp - zanak_sf_cp
        cp_loss = max(0, cp_loss_raw)

        return {
            "sf_depth": actual_depth,
            "sf_multipv_count": len(table),
            "sf_best_move": sf_best_move,
            "best_sf_cp": best_sf_cp,
            "sf_best_score_type": sf_best_score_type,
            "sf_best_score_value": sf_best_score_value,
            "sf_rank": sf_rank,
            "sf_rank_label": f"{multipv + 1}+" if outside_top_20 else str(sf_rank),
            "outside_top_20": outside_top_20,
            "zanak_sf_cp": zanak_sf_cp,
            "zanak_score_type": zanak_score_type,
            "zanak_score_value": zanak_score_value,
            "cp_loss_raw": cp_loss_raw,
            "cp_loss": cp_loss,
        }

    def close(self) -> None:
        if self._proc.poll() is None:
            try:
                self._send("quit")
                self._proc.wait(timeout=2.0)
            except (OSError, subprocess.TimeoutExpired):
                self._proc.kill()
                self._proc.wait()
        for stream in (self._proc.stdin, self._proc.stdout, self._proc.stderr):
            if stream:
                try:
                    stream.close()
                except OSError:
                    pass

    def __enter__(self) -> "StockfishClient":
        return self

    def __exit__(self, *args: Any) -> None:
        self.close()


def classify_rank_bucket(sf_rank: int, outside_top_20: bool) -> str:
    if outside_top_20 or sf_rank > 20:
        return "21+"
    if sf_rank == 1:
        return "1"
    if sf_rank <= 3:
        return "2-3"
    if sf_rank <= 5:
        return "4-5"
    if sf_rank <= 10:
        return "6-10"
    return "11-20"


def classify_ply_bucket(ply: int) -> str:
    if ply == 0:
        return "ply 0"
    if ply <= 2:
        return "ply 1-2"
    if ply <= 5:
        return "ply 3-5"
    return "ply 6+"


def classify_transition(row: dict[str, Any]) -> str:
    prev = bool(row["prev_was_capture"])
    curr = bool(row["is_capture"])
    if prev and curr:
        return "capture -> capture"
    if prev and not curr:
        return "capture -> quiet"
    if not prev and curr:
        return "quiet -> capture"
    return "quiet -> quiet"


def classify_policy_category(row: dict[str, Any]) -> str:
    if row["in_check_before"]:
        return "check_evasion"
    if row["is_promotion"]:
        return "promotion"
    if row["is_capture"]:
        return "recapture" if row.get("is_recapture") else "capture"
    if row["from_attacked_before"]:
        return "piece_evasion"
    if row["is_check"]:
        return "quiet_check"
    if row.get("moved_piece_type") == "pawn" and not row["dest_attacked_after"]:
        return "safe_pawn_push"
    return "fallback_likely"


def classify_see_bucket(see_cp: int) -> str:
    if see_cp < 0:
        return "see_cp < 0"
    if see_cp == 0:
        return "see_cp == 0"
    return "see_cp > 0"


def classify_eval_delta_bucket(delta: int, shock_cp: int = DEFAULT_SHOCK_CP) -> str:
    if delta <= -shock_cp:
        return f"shock_drop (<= -{shock_cp})"
    if delta < 0:
        return f"mild_drop (-{shock_cp - 1}..-1)"
    if delta < shock_cp:
        return f"mild_gain (0..+{shock_cp - 1})"
    return f"shock_gain (>= +{shock_cp})"


def enrich_row(spine_row: dict[str, Any], sf_row: dict[str, Any]) -> dict[str, Any]:
    """Combine one ZanakSpineDump record with Stockfish MultiPV telemetry and bucket labels."""
    merged = {**spine_row, **sf_row}
    merged["ply_bucket"] = classify_ply_bucket(int(merged["ply"]))
    merged["transition"] = classify_transition(merged)
    merged["policy_category"] = classify_policy_category(merged)
    merged["see_bucket"] = classify_see_bucket(int(merged["see_cp"]))
    merged["shock_abs"] = abs(int(merged["static_eval_delta"])) >= DEFAULT_SHOCK_CP
    merged["delta_sign"] = "positive" if int(merged["static_eval_delta"]) >= 0 else "negative"
    merged["delta_bucket"] = classify_eval_delta_bucket(int(merged["static_eval_delta"]))
    merged["turn_role"] = "player (ply%2==0)" if int(merged["ply"]) % 2 == 0 else "opponent (ply%2==1)"
    merged["rank_bucket"] = classify_rank_bucket(int(merged["sf_rank"]), bool(merged["outside_top_20"]))
    merged["is_top1"] = int(merged["sf_rank"]) == 1
    merged["is_top3"] = int(merged["sf_rank"]) <= 3
    merged["is_top5"] = int(merged["sf_rank"]) <= 5
    merged["is_top10"] = int(merged["sf_rank"]) <= 10
    merged["is_severe_blunder"] = int(merged["cp_loss"]) >= 150
    merged["is_catastrophic"] = int(merged["cp_loss"]) >= 300
    return merged


def summarize_group(rows: list[dict[str, Any]]) -> dict[str, Any]:
    """Compute rank & centipawn-loss summary statistics for a slice of rows."""
    n = len(rows)
    if n == 0:
        return {
            "count": 0,
            "mean_rank": 0.0,
            "median_rank": 0.0,
            "top1_pct": 0.0,
            "top3_pct": 0.0,
            "top5_pct": 0.0,
            "top10_pct": 0.0,
            "top20_pct": 0.0,
            "outside20_pct": 0.0,
            "mean_cp_loss": 0.0,
            "median_cp_loss": 0.0,
            "p75_cp_loss": 0.0,
            "p90_cp_loss": 0.0,
            "max_cp_loss": 0,
            "blunder150_pct": 0.0,
            "blunder300_pct": 0.0,
        }

    ranks = [int(r["sf_rank"]) for r in rows]
    losses = sorted(int(r["cp_loss"]) for r in rows)
    p75_idx = min(n - 1, int(0.75 * (n - 1)))
    p90_idx = min(n - 1, int(0.90 * (n - 1)))

    return {
        "count": n,
        "mean_rank": round(statistics.fmean(ranks), 2),
        "median_rank": round(statistics.median(ranks), 2),
        "top1_pct": round(100.0 * sum(1 for r in rows if r["is_top1"]) / n, 1),
        "top3_pct": round(100.0 * sum(1 for r in rows if r["is_top3"]) / n, 1),
        "top5_pct": round(100.0 * sum(1 for r in rows if r["is_top5"]) / n, 1),
        "top10_pct": round(100.0 * sum(1 for r in rows if r["is_top10"]) / n, 1),
        "top20_pct": round(100.0 * sum(1 for r in rows if not r["outside_top_20"]) / n, 1),
        "outside20_pct": round(100.0 * sum(1 for r in rows if r["outside_top_20"]) / n, 1),
        "mean_cp_loss": round(statistics.fmean(losses), 1),
        "median_cp_loss": round(statistics.median(losses), 1),
        "p75_cp_loss": losses[p75_idx],
        "p90_cp_loss": losses[p90_idx],
        "max_cp_loss": losses[-1],
        "blunder150_pct": round(100.0 * sum(1 for r in rows if r["is_severe_blunder"]) / n, 1),
        "blunder300_pct": round(100.0 * sum(1 for r in rows if r["is_catastrophic"]) / n, 1),
    }


def _render_markdown_table(title: str, ordered_groups: list[tuple[str, list[dict[str, Any]]]], total: int) -> list[str]:
    lines = [
        f"## {title}",
        "",
        "| Slice | N | Share% | Mean Rank (21+=21) | Med Rank | Top1% | Top3% | Top10% | 21+% | Mean CP Loss | Med CP Loss | P90 CP Loss | Max CP Loss | >=150cp% | >=300cp% |",
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for label, slice_rows in ordered_groups:
        s = summarize_group(slice_rows)
        share = round(100.0 * s["count"] / total, 1) if total else 0.0
        lines.append(
            f"| {label} | {s['count']} | {share:.1f}% | {s['mean_rank']:.2f} | {s['median_rank']:.1f} | "
            f"{s['top1_pct']:.1f}% | {s['top3_pct']:.1f}% | {s['top10_pct']:.1f}% | {s['outside20_pct']:.1f}% | "
            f"{s['mean_cp_loss']:.1f} | {s['median_cp_loss']:.1f} | {s['p90_cp_loss']} | {s['max_cp_loss']} | "
            f"{s['blunder150_pct']:.1f}% | {s['blunder300_pct']:.1f}% |"
        )
    lines.append("")
    return lines


def build_markdown_summary(rows: list[dict[str, Any]], depth: int = 10, multipv: int = 20) -> str:
    """Render a detailed statistical analysis report in Markdown."""
    total = len(rows)
    fen_count = len({int(r["fen_idx"]) for r in rows})
    overall = summarize_group(rows)

    lines: list[str] = [
        "# Zanak 25-Step Spine Move-Rank & Centipawn-Loss Telemetry",
        "",
        f"- **Oracle:** Stockfish (`MultiPV {multipv}`, `depth {depth}`; out-of-top-20 evaluated via `searchmoves`)",
        f"- **Positions:** {fen_count} curated EPD/FENs, **{total}** total spine nodes evaluated",
        f"- **Overall Top-1 / Top-3 / Top-10 / Top-20 / 21+:** "
        f"{overall['top1_pct']:.1f}% / {overall['top3_pct']:.1f}% / {overall['top10_pct']:.1f}% / "
        f"{overall['top20_pct']:.1f}% / {overall['outside20_pct']:.1f}%",
        f"- **Overall CP Loss:** mean **{overall['mean_cp_loss']:.1f} cp**, median **{overall['median_cp_loss']:.1f} cp**, "
        f"p75 **{overall['p75_cp_loss']} cp**, p90 **{overall['p90_cp_loss']} cp**, max **{overall['max_cp_loss']} cp**",
        f"- **Severe Blunders (>=150 cp / >=300 cp):** {overall['blunder150_pct']:.1f}% / {overall['blunder300_pct']:.1f}%",
        "",
    ]

    # 1. Rank distribution
    rank_order = ["1", "2-3", "4-5", "6-10", "11-20", "21+"]
    lines.extend(_render_markdown_table(
        "1. Overall Stockfish Rank Distribution",
        [(b, [r for r in rows if r["rank_bucket"] == b]) for b in rank_order],
        total,
    ))

    # 2. Ply depth buckets & per-ply
    ply_buckets = ["ply 0", "ply 1-2", "ply 3-5", "ply 6+"]
    lines.extend(_render_markdown_table(
        "2a. Breakdown by Spine Ply Depth Bucket",
        [(b, [r for r in rows if r["ply_bucket"] == b]) for b in ply_buckets],
        total,
    ))
    per_ply = sorted({int(r["ply"]) for r in rows})
    lines.extend(_render_markdown_table(
        "2b. Per-Ply Breakdown (0..9)",
        [(f"ply {p}", [r for r in rows if int(r["ply"]) == p]) for p in per_ply],
        total,
    ))

    # 3. Move transition & policy step category
    transitions = ["capture -> capture", "capture -> quiet", "quiet -> capture", "quiet -> quiet"]
    lines.extend(_render_markdown_table(
        "3a. Breakdown by Move Transition",
        [(t, [r for r in rows if r["transition"] == t]) for t in transitions],
        total,
    ))
    categories = [
        "check_evasion",
        "promotion",
        "recapture",
        "capture",
        "piece_evasion",
        "quiet_check",
        "safe_pawn_push",
        "fallback_likely",
    ]
    lines.extend(_render_markdown_table(
        "3b. Breakdown by Policy Step Category (incl. Check / Evasion / Promotion / Fallback)",
        [(c, [r for r in rows if r["policy_category"] == c]) for c in categories],
        total,
    ))

    # 4. Destination defense & SEE
    lines.extend(_render_markdown_table(
        "4a. Breakdown by Destination Attack Status (`dest_attacked_after`)",
        [
            ("dest_attacked_after == true", [r for r in rows if r["dest_attacked_after"]]),
            ("dest_attacked_after == false", [r for r in rows if not r["dest_attacked_after"]]),
        ],
        total,
    ))
    see_buckets = ["see_cp < 0", "see_cp == 0", "see_cp > 0"]
    lines.extend(_render_markdown_table(
        "4b. Breakdown by Static Exchange Evaluation (`see_cp`)",
        [(b, [r for r in rows if r["see_bucket"] == b]) for b in see_buckets],
        total,
    ))
    cross_dest_see = [
        ("quiet + dest_attacked + see<0", [r for r in rows if not r["is_capture"] and r["dest_attacked_after"] and int(r["see_cp"]) < 0]),
        ("quiet + dest_attacked + see>=0", [r for r in rows if not r["is_capture"] and r["dest_attacked_after"] and int(r["see_cp"]) >= 0]),
        ("quiet + dest_safe", [r for r in rows if not r["is_capture"] and not r["dest_attacked_after"]]),
        ("capture + see < 0", [r for r in rows if r["is_capture"] and int(r["see_cp"]) < 0]),
        ("capture + see == 0", [r for r in rows if r["is_capture"] and int(r["see_cp"]) == 0]),
        ("capture + see > 0", [r for r in rows if r["is_capture"] and int(r["see_cp"]) > 0]),
    ]
    lines.extend(_render_markdown_table("4c. Cross-Tab: Capture vs Quiet x Defense / SEE", cross_dest_see, total))

    # 5. Static eval delta & shock threshold
    lines.extend(_render_markdown_table(
        f"5a. Breakdown by Shock Magnitude (`|static_eval_delta| >= {DEFAULT_SHOCK_CP}`)",
        [
            (f"shock (|delta| >= {DEFAULT_SHOCK_CP})", [r for r in rows if r["shock_abs"]]),
            (f"non-shock (|delta| < {DEFAULT_SHOCK_CP})", [r for r in rows if not r["shock_abs"]]),
        ],
        total,
    ))
    delta_buckets = [
        f"shock_drop (<= -{DEFAULT_SHOCK_CP})",
        f"mild_drop (-{DEFAULT_SHOCK_CP - 1}..-1)",
        f"mild_gain (0..+{DEFAULT_SHOCK_CP - 1})",
        f"shock_gain (>= +{DEFAULT_SHOCK_CP})",
    ]
    lines.extend(_render_markdown_table(
        "5b. Breakdown by Signed `static_eval_delta`",
        [(b, [r for r in rows if r["delta_bucket"] == b]) for b in delta_buckets],
        total,
    ))

    # 6. Turn along spine
    turns = ["player (ply%2==0)", "opponent (ply%2==1)"]
    lines.extend(_render_markdown_table(
        "6a. Breakdown by Spine Turn (`ply % 2`)",
        [(t, [r for r in rows if r["turn_role"] == t]) for t in turns],
        total,
    ))
    turn_x_ply = [
        (f"{t} | {pb}", [r for r in rows if r["turn_role"] == t and r["ply_bucket"] == pb])
        for pb in ply_buckets
        for t in turns
    ]
    lines.extend(_render_markdown_table("6b. Cross-Tab: Turn x Ply Depth Bucket", turn_x_ply, total))

    # 7. Threatened piece before move
    lines.extend(_render_markdown_table(
        "7. Breakdown by Threatened Material Before Move (`max_threatened_before`)",
        [
            ("max_threatened_before == 0", [r for r in rows if int(r["max_threatened_before"]) == 0]),
            ("1..350 cp (pawn/minor)", [r for r in rows if 0 < int(r["max_threatened_before"]) <= 350]),
            ("> 350 cp (rook/queen)", [r for r in rows if int(r["max_threatened_before"]) > 350]),
        ],
        total,
    ))

    # 8. Worst nodes
    worst = sorted(rows, key=lambda r: (-int(r["cp_loss"]), -int(r["sf_rank"]), int(r["fen_idx"]), int(r["ply"])))[:20]
    lines.extend([
        "## 8. Top 20 Largest Centipawn-Loss Spine Nodes",
        "",
        "| FEN# | Ply | Zanak | SF Best | SF Rank | CP Loss | Transition | Category | `dest_atk` | `see_cp` | `delta` | `max_thr` | FEN |",
        "|---:|---:|---|---|---:|---:|---|---|---|---:|---:|---:|---|",
    ])
    for r in worst:
        lines.append(
            f"| {r['fen_idx']} | {r['ply']} | `{r['zanak_move']}` | `{r['sf_best_move']}` | {r['sf_rank_label']} | "
            f"{r['cp_loss']} | {r['transition']} | {r['policy_category']} | {str(r['dest_attacked_after']).lower()} | "
            f"{r['see_cp']} | {r['static_eval_delta']} | {r['max_threatened_before']} | `{r['fen']}` |"
        )
    lines.append("")
    return "\n".join(lines)


def run_zanak_spine_dump(epd_path: Path, dump_jsonl_path: Path, max_plies: int) -> list[dict[str, Any]]:
    """Build (if needed) and invoke ZanakSpineDump to trace Zanak's 25-step linear spines."""
    build_cmd = ["dotnet", "build", str(DEFAULT_DUMPER_CSPROJ), "-c", "Release", "--nologo", "-v", "q"]
    subprocess.run(build_cmd, cwd=str(REPO_ROOT), check=True)
    run_cmd = [
        "dotnet",
        "exec",
        str(DEFAULT_DUMPER_DLL),
        "--input",
        str(epd_path),
        "--output",
        str(dump_jsonl_path),
        "--max-plies",
        str(max_plies),
    ]
    subprocess.run(run_cmd, cwd=str(REPO_ROOT), check=True)
    return load_jsonl(dump_jsonl_path)


def load_jsonl(path: Path) -> list[dict[str, Any]]:
    with Path(path).open("r", encoding="utf-8-sig") as handle:
        return [json.loads(line) for line in handle if line.strip()]


def write_jsonl(path: Path, rows: list[dict[str, Any]]) -> None:
    with Path(path).open("w", encoding="utf-8", newline="\n") as handle:
        for row in rows:
            handle.write(json.dumps(row, separators=(",", ":")) + "\n")


def write_csv(path: Path, rows: list[dict[str, Any]]) -> None:
    if not rows:
        Path(path).write_text("", encoding="utf-8")
        return
    fieldnames = list(rows[0].keys())
    with Path(path).open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Gather Stockfish MultiPV=20 telemetry along Zanak's 25-step spine.")
    parser.add_argument("--epd", type=Path, default=DEFAULT_EPD)
    parser.add_argument("--dump-jsonl", type=Path, default=DEFAULT_OUT_DIR / "spine_dump.jsonl")
    parser.add_argument("--skip-dump", action="store_true", help="Reuse existing --dump-jsonl instead of re-running ZanakSpineDump.")
    parser.add_argument("--stockfish", type=Path, default=DEFAULT_STOCKFISH)
    parser.add_argument("--max-plies", type=int, default=10)
    parser.add_argument("--depth", type=int, default=10)
    parser.add_argument("--multipv", type=int, default=20)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--hash-mb", type=int, default=64)
    parser.add_argument("--out-jsonl", type=Path, default=DEFAULT_OUT_DIR / "telemetry_results.jsonl")
    parser.add_argument("--out-csv", type=Path, default=DEFAULT_OUT_DIR / "telemetry_results.csv")
    parser.add_argument("--out-summary", type=Path, default=DEFAULT_OUT_DIR / "telemetry_summary.md")
    args = parser.parse_args(argv)

    t0 = time.monotonic()
    if args.skip_dump:
        spine_rows = load_jsonl(args.dump_jsonl)
    else:
        spine_rows = run_zanak_spine_dump(args.epd, args.dump_jsonl, args.max_plies)

    print(f"Loaded {len(spine_rows)} Zanak spine nodes. Querying Stockfish ({args.stockfish.name}, MultiPV={args.multipv}, depth={args.depth}, threads={args.threads})...", file=sys.stderr)

    enriched: list[dict[str, Any]] = []
    with StockfishClient(args.stockfish, threads=args.threads, hash_mb=args.hash_mb) as sf:
        for idx, row in enumerate(spine_rows, start=1):
            sf_data = sf.evaluate_position(row["fen"], row["zanak_move"], depth=args.depth, multipv=args.multipv)
            enriched.append(enrich_row(row, sf_data))
            if idx % 25 == 0 or idx == len(spine_rows):
                print(f"  [{idx}/{len(spine_rows)}] elapsed {time.monotonic() - t0:.1f}s", file=sys.stderr)

    write_jsonl(args.out_jsonl, enriched)
    write_csv(args.out_csv, enriched)
    summary_md = build_markdown_summary(enriched, depth=args.depth, multipv=args.multipv)
    args.out_summary.write_text(summary_md, encoding="utf-8", newline="\n")
    print(f"Done in {time.monotonic() - t0:.1f}s. Wrote {args.out_jsonl}, {args.out_csv}, {args.out_summary}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
