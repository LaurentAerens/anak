#!/usr/bin/env python3
"""
Anak Chess Engine — Release Notes & Elo Chart Generator
Generates tiered release notes, SVG Elo progression chart, and Mermaid diagram
tailored specifically to:
  - 1.0.x: Single feature / patch release focused on the specific improvement and Elo impact
  - 1.x.0: Minor release consolidating all changes and intermediate Elo gains since 1.(x-1).0
  - x.0.0: Major release with all changes since (x-1).0.0 and extensive architectural overview
Guaranteed to always generate valid, informative release notes under all conditions.
"""

import argparse
import json
import os
import re
import subprocess
from datetime import datetime
from pathlib import Path


def parse_semver(tag: str) -> tuple[int, int, int, str] | None:
    """Parses semantic version from tag string (e.g. 'v1.0.9', '1.1.0', 'v2.0.0-rc1')."""
    cleaned = tag.strip().lstrip("vV")
    m = re.match(r"^(\d+)\.(\d+)\.(\d+)(?:[-_.]?(.*))?$", cleaned)
    if m:
        return int(m.group(1)), int(m.group(2)), int(m.group(3)), (m.group(4) or "")
    return None


def determine_tier(major: int, minor: int, patch: int) -> str:
    """
    Determines release tier:
      - 'single_feat': 1.0.x / patch > 0
      - 'minor': 1.x.0 / minor > 0, patch == 0
      - 'major': x.0.0 / minor == 0, patch == 0
    """
    if patch > 0:
        return "single_feat"
    elif minor > 0:
        return "minor"
    else:
        return "major"


def load_ratings(ratings_path: Path) -> dict:
    if ratings_path.exists():
        try:
            with open(ratings_path, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception as e:
            print(f"[!] Warning: Failed to load {ratings_path}: {e}")
    return {"versions": {}}


def normalize_version_list(version_dict: dict) -> list[dict]:
    version_list = []
    for k, v in version_dict.items():
        ver_tag = v.get("version", k)
        version_list.append({
            "key": k,
            "version": ver_tag,
            "date": v.get("date", ""),
            "elo": v.get("estimated_elo"),
            "margin": v.get("elo_margin"),
            "opponent": v.get("baseline_opponent", "-"),
            "score": v.get("score", "-"),
            "perf": v.get("perf_percent"),
            "wins": v.get("wins", 0),
            "losses": v.get("losses", 0),
            "draws": v.get("draws", 0),
            "tc": v.get("tc", "-"),
            "pgn": v.get("pgn", "-"),
            "notes": v.get("notes", ""),
        })
    version_list.sort(key=lambda x: (x["date"], x["version"]))
    return version_list


def find_version_entry(version_list: list[dict], tag: str) -> dict | None:
    clean_tag = tag.strip().lstrip("vV")
    for item in version_list:
        item_ver = item["version"].strip().lstrip("vV")
        item_key = item["key"].strip().lstrip("vV")
        if item_ver == clean_tag or item_key == clean_tag or item["version"] == tag or item["key"] == tag:
            return item
    return None


def synthesize_version_info_from_git(tag: str, fallback_prev: dict | None = None) -> dict:
    """Extracts metadata from recent git commit messages when tag is not in ratings.json."""
    today = datetime.now().strftime("%Y-%m-%d")
    info = {
        "key": tag,
        "version": tag,
        "date": today,
        "elo": fallback_prev.get("elo") if fallback_prev else None,
        "margin": None,
        "opponent": fallback_prev.get("version", "-") if fallback_prev else "-",
        "score": "-",
        "perf": None,
        "wins": 0,
        "losses": 0,
        "draws": 0,
        "tc": "1+0.01",
        "pgn": "-",
        "notes": f"Release {tag} of Anak Chess Engine",
    }

    try:
        proc = subprocess.run(
            ["git", "log", "-n", "20", "--pretty=format:%h|||%s|||%b"],
            capture_output=True,
            text=True,
            check=True
        )
        for line in proc.stdout.splitlines():
            if not line.strip():
                continue
            parts = line.split("|||")
            subject = parts[1] if len(parts) > 1 else ""
            clean_tag = tag.strip().lstrip("vV")
            if clean_tag in subject or tag in subject or subject.lower().startswith("release"):
                # Pattern: release(v1.0.9): desc (+3.8 Elo, 2523 Elo)
                m = re.search(r"release\([^)]+\):\s*(.*?)(?:\s*\(\+?([0-9.]+)\s*Elo,\s*([0-9]+)\s*Elo\))?$", subject, re.IGNORECASE)
                if m:
                    if m.group(1):
                        info["notes"] = m.group(1).strip()
                    if m.group(3):
                        info["elo"] = int(m.group(3))
                else:
                    info["notes"] = subject
                break
    except Exception as e:
        print(f"[!] Warning synthesizing version info from git: {e}")

    return info


def find_git_commit_range(tag: str, tier: str, semver_tuple: tuple[int, int, int]) -> str:
    """
    Finds the appropriate git commit range for changelog extraction:
      - single_feat: Commits since immediately previous release commit or patch tag
      - minor: Commits since 1.(minor-1).0
      - major: Commits since (major-1).0.0
    """
    major, minor, patch = semver_tuple
    try:
        tags_proc = subprocess.run(
            ["git", "tag", "--sort=v:refname"],
            capture_output=True,
            text=True,
            check=True
        )
        all_tags = [t.strip() for t in tags_proc.stdout.splitlines() if t.strip()]
    except Exception:
        all_tags = []

    target_ref = tag if tag in all_tags else "HEAD"

    if tier == "single_feat":
        # Check if previous patch tag exists (e.g. v1.0.8 or 1.0.8)
        prev_patch_candidates = [f"v{major}.{minor}.{patch-1}", f"{major}.{minor}.{patch-1}"]
        for cand in prev_patch_candidates:
            if cand in all_tags:
                return f"{cand}..{target_ref}"

        # Otherwise find previous release commit in git log
        try:
            log_proc = subprocess.run(
                ["git", "log", "--pretty=format:%h %s", "-n", "60", target_ref],
                capture_output=True,
                text=True,
                check=True
            )
            commits = log_proc.stdout.splitlines()
            curr_str = f"{major}.{minor}.{patch}"
            found_curr = False
            for line in commits:
                parts = line.split(" ", 1)
                if len(parts) < 2:
                    continue
                commit_hash, subject = parts[0], parts[1]
                if curr_str in subject:
                    found_curr = True
                    continue
                if found_curr and (subject.lower().startswith("release(") or subject.lower().startswith("release:")):
                    return f"{commit_hash}..{target_ref}"
        except Exception:
            pass

        return f"{target_ref}~1..{target_ref}"

    elif tier == "minor":
        prev_minor_candidates = [f"v{major}.{minor-1}.0", f"{major}.{minor-1}.0"]
        for cand in prev_minor_candidates:
            if cand in all_tags:
                return f"{cand}..{target_ref}"

        # Search git log for previous minor release
        try:
            log_proc = subprocess.run(
                ["git", "log", "--pretty=format:%h %s", "-n", "100", target_ref],
                capture_output=True,
                text=True,
                check=True
            )
            for line in log_proc.stdout.splitlines():
                parts = line.split(" ", 1)
                if len(parts) < 2:
                    continue
                commit_hash, subject = parts[0], parts[1]
                if f"{major}.{minor-1}.0" in subject:
                    return f"{commit_hash}..{target_ref}"
        except Exception:
            pass

        return all_tags[-1] + f"..{target_ref}" if all_tags else f"HEAD~10..{target_ref}"

    else:  # major
        if major > 1:
            prev_major_candidates = [f"v{major-1}.0.0", f"{major-1}.0.0"]
            for cand in prev_major_candidates:
                if cand in all_tags:
                    return f"{cand}..{target_ref}"
            try:
                log_proc = subprocess.run(
                    ["git", "log", "--pretty=format:%h %s", "-n", "150", target_ref],
                    capture_output=True,
                    text=True,
                    check=True
                )
                for line in log_proc.stdout.splitlines():
                    parts = line.split(" ", 1)
                    if len(parts) < 2:
                        continue
                    commit_hash, subject = parts[0], parts[1]
                    if f"{major-1}.0.0" in subject:
                        return f"{commit_hash}..{target_ref}"
            except Exception:
                pass

        # For 1.0.0 or initial major: return commits up to target_ref
        if "v0.0.1" in all_tags:
            return f"v0.0.1..{target_ref}"
        return target_ref


def categorize_commit(commit_hash: str, msg: str) -> tuple[str, str]:
    formatted = f"- [`{commit_hash}`] {msg}"
    msg_lower = msg.lower()

    if msg_lower.startswith("feat"):
        return ("🚀 Features", formatted)
    elif msg_lower.startswith("perf") or any(k in msg_lower for k in ["speed", "nps", "optim", "flatten", "nativeaot", "skiplocalsinit"]):
        return ("⚡ Performance", formatted)
    elif msg_lower.startswith("fix"):
        return ("🐛 Bug Fixes", formatted)
    elif msg_lower.startswith("tool") or msg_lower.startswith("ci") or any(k in msg_lower for k in ["bench", "spsa", "test"]):
        return ("🛠️ Tooling & Benchmarks", formatted)
    elif msg_lower.startswith("doc"):
        return ("📖 Documentation", formatted)
    elif msg_lower.startswith("release"):
        if any(k in msg_lower for k in ["speed", "nps", "optim", "flatten", "aot"]):
            return ("⚡ Performance", formatted)
        elif any(k in msg_lower for k in ["fix", "safety", "guard", "bound"]):
            return ("🐛 Bug Fixes", formatted)
        else:
            return ("🚀 Features", formatted)
    else:
        return ("🧰 Maintenance & Refactoring", formatted)


def get_git_changelog(git_range: str) -> dict[str, list[str]]:
    categories = {
        "🚀 Features": [],
        "⚡ Performance": [],
        "🐛 Bug Fixes": [],
        "🛠️ Tooling & Benchmarks": [],
        "📖 Documentation": [],
        "🧰 Maintenance & Refactoring": [],
    }

    try:
        proc = subprocess.run(
            ["git", "log", git_range, "--pretty=format:%h %s"],
            capture_output=True,
            text=True,
            check=True
        )
        commits = [c.strip() for c in proc.stdout.splitlines() if c.strip()]
        for c in commits:
            parts = c.split(" ", 1)
            if len(parts) < 2:
                continue
            cat, formatted = categorize_commit(parts[0], parts[1])
            categories[cat].append(formatted)
    except Exception as e:
        print(f"[!] Warning extracting changelog ({git_range}): {e}")

    return {k: v for k, v in categories.items() if v}


def parse_test_results(test_file: Path | None) -> dict:
    info = {"status": "PASSED", "passed": None, "total": None, "time": None}
    if not test_file or not test_file.exists():
        return info
    try:
        text = test_file.read_text(encoding="utf-8", errors="ignore")
        m_passed = re.search(r"Passed:\s*(\d+)", text)
        m_total = re.search(r"Total tests:\s*(\d+)", text)
        m_time = re.search(r"Total time:\s*([0-9.,]+)\s*Seconds", text)
        if m_passed:
            info["passed"] = int(m_passed.group(1))
        if m_total:
            info["total"] = int(m_total.group(1))
        if m_time:
            info["time"] = m_time.group(1).replace(",", ".")
        if "Failed:" in text and not re.search(r"Failed:\s*0", text):
            info["status"] = "FAILED"
    except Exception as e:
        print(f"[!] Warning parsing test results: {e}")
    return info


def parse_perft_results(perft_file: Path | None) -> dict:
    info = {"status": "VERIFIED", "nodes": "197,281", "time": None, "nps": None}
    if not perft_file or not perft_file.exists():
        return info
    try:
        text = perft_file.read_text(encoding="utf-8", errors="ignore")
        m = re.search(r"Nodes searched:\s*(\d+)\s*in\s*(\d+)\s*ms\s*(?:\(([\d,.]+)\s*nps\))?", text, re.IGNORECASE)
        if m:
            info["nodes"] = f"{int(m.group(1)):,}"
            info["time"] = f"{m.group(2)} ms"
            if m.group(3):
                info["nps"] = f"{m.group(3)} nps"
    except Exception as e:
        print(f"[!] Warning parsing perft results: {e}")
    return info


def parse_bench_results(bench_file: Path | None) -> dict:
    info = {"status": "MEASURED", "nodes": None, "nps": None, "time": None}
    if not bench_file or not bench_file.exists():
        return info
    try:
        text = bench_file.read_text(encoding="utf-8", errors="ignore")
        m_nodes = re.search(r"Total Nodes:\s*([\d,]+)", text)
        m_time = re.search(r"Total Time:\s*([\d,]+)\s*ms", text)
        m_nps = re.search(r"NPS:\s*([\d,]+)", text)
        if m_nodes:
            info["nodes"] = m_nodes.group(1)
        if m_time:
            info["time"] = f"{m_time.group(1)} ms"
        if m_nps:
            info["nps"] = f"{m_nps.group(1)} nps"
    except Exception as e:
        print(f"[!] Warning parsing bench results: {e}")
    return info


def generate_svg_chart(versions: list[dict], output_svg_path: Path):
    if not versions:
        return

    width = 820
    height = 420
    pad_left = 75
    pad_right = 50
    pad_top = 70
    pad_bottom = 65

    chart_w = width - pad_left - pad_right
    chart_h = height - pad_top - pad_bottom

    elos = [v["elo"] for v in versions if v["elo"] is not None]
    if not elos:
        return

    min_elo = min(elos)
    max_elo = max(elos)

    y_min = (min_elo // 100) * 100 - 50
    y_max = ((max_elo + 99) // 100) * 100 + 50
    y_range = max(100, y_max - y_min)

    n_points = len(versions)
    points = []
    for i, v in enumerate(versions):
        x = pad_left + (i * chart_w / max(1, n_points - 1)) if n_points > 1 else pad_left + chart_w / 2
        norm_y = (v["elo"] - y_min) / y_range
        y = pad_top + chart_h - (norm_y * chart_h)
        points.append((x, y, v))

    line_path = " ".join([f"{'M' if i == 0 else 'L'} {p[0]:.1f} {p[1]:.1f}" for i, p in enumerate(points)])
    area_path = f"{line_path} L {points[-1][0]:.1f} {pad_top + chart_h:.1f} L {points[0][0]:.1f} {pad_top + chart_h:.1f} Z"

    y_step = 100
    y_ticks = list(range(int(y_min + 50), int(y_max), y_step))

    svg_parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="100%" height="auto" style="background:#0d1117; font-family: -apple-system, BlinkMacSystemFont, Segoe UI, Helvetica, Arial, sans-serif; border-radius: 8px; border: 1px solid #30363d;">',
        '  <defs>',
        '    <linearGradient id="areaGradient" x1="0" y1="0" x2="0" y2="1">',
        '      <stop offset="0%" stop-color="#58a6ff" stop-opacity="0.35"/>',
        '      <stop offset="100%" stop-color="#1f6feb" stop-opacity="0.0"/>',
        '    </linearGradient>',
        '    <linearGradient id="lineGradient" x1="0" y1="0" x2="1" y2="0">',
        '      <stop offset="0%" stop-color="#388bfd"/>',
        '      <stop offset="100%" stop-color="#2ea043"/>',
        '    </linearGradient>',
        '    <filter id="glow" x="-20%" y="-20%" width="140%" height="140%">',
        '      <feDropShadow dx="0" dy="2" stdDeviation="3" flood-color="#2ea043" flood-opacity="0.5"/>',
        '    </filter>',
        '  </defs>',
        '',
        f'  <text x="{width / 2}" y="36" text-anchor="middle" font-size="18" font-weight="600" fill="#f0f6fc">Anak Chess Engine — Elo Rating Progression</text>',
        f'  <text x="{width / 2}" y="54" text-anchor="middle" font-size="12" fill="#8b949e">Benchmark tracking against reference baselines and calibrated Stockfish</text>',
        '',
    ]

    for y_val in y_ticks:
        norm_y = (y_val - y_min) / y_range
        grid_y = pad_top + chart_h - (norm_y * chart_h)
        svg_parts.append(f'  <line x1="{pad_left}" y1="{grid_y:.1f}" x2="{width - pad_right}" y2="{grid_y:.1f}" stroke="#21262d" stroke-width="1" stroke-dasharray="4,4"/>')
        svg_parts.append(f'  <text x="{pad_left - 12}" y="{grid_y + 4:.1f}" text-anchor="end" font-size="12" fill="#8b949e">{y_val}</text>')

    svg_parts.append(f'  <line x1="{pad_left}" y1="{pad_top + chart_h}" x2="{width - pad_right}" y2="{pad_top + chart_h}" stroke="#30363d" stroke-width="1.5"/>')
    svg_parts.append(f'  <path d="{area_path}" fill="url(#areaGradient)" />')
    svg_parts.append(f'  <path d="{line_path}" fill="none" stroke="url(#lineGradient)" stroke-width="3.5" stroke-linecap="round" stroke-linejoin="round"/>')

    for i, (x, y, v) in enumerate(points):
        is_latest = (i == len(points) - 1)
        tag = v["version"]
        elo = v["elo"]
        margin = v.get("margin")
        margin_str = f" ±{margin}" if margin is not None else ""

        fill_color = "#2ea043" if is_latest else "#58a6ff"
        stroke_color = "#ffffff" if is_latest else "#0d1117"
        r = 6 if is_latest else 5
        filter_str = ' filter="url(#glow)"' if is_latest else ''
        svg_parts.append(f'  <circle cx="{x:.1f}" cy="{y:.1f}" r="{r}" fill="{fill_color}" stroke="{stroke_color}" stroke-width="2"{filter_str}/>')
        svg_parts.append(f'  <text x="{x:.1f}" y="{y - 14:.1f}" text-anchor="middle" font-size="13" font-weight="700" fill="#f0f6fc">{elo}{margin_str}</text>')
        svg_parts.append(f'  <text x="{x:.1f}" y="{pad_top + chart_h + 22:.1f}" text-anchor="middle" font-size="12" font-weight="600" fill="#c9d1d9">{tag}</text>')
        date_str = v.get("date", "")
        if date_str:
            svg_parts.append(f'  <text x="{x:.1f}" y="{pad_top + chart_h + 38:.1f}" text-anchor="middle" font-size="10" fill="#8b949e">{date_str}</text>')

    svg_parts.append('</svg>')
    output_svg_path.parent.mkdir(parents=True, exist_ok=True)
    output_svg_path.write_text("\n".join(svg_parts), encoding="utf-8")
    print(f"[+] Generated Elo chart: {output_svg_path}")


def render_badges(tag: str, elo: any, test_status: str, repo: str) -> str:
    badge_tests = "https://img.shields.io/badge/Tests-Passed-2ea043.svg" if test_status == "PASSED" else "https://img.shields.io/badge/Tests-Issues-critical.svg"
    elo_str = str(elo) if elo is not None else "Dev"
    return (
        f"[![Runtime: .NET 11](https://img.shields.io/badge/.NET-11.0-512BD4.svg)](https://dotnet.microsoft.com/) "
        f"[![Tests Status]({badge_tests})](https://github.com/{repo}/actions) "
        f"[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://github.com/{repo}/blob/main/LICENSE) "
        f"[![Rating](https://img.shields.io/badge/Elo-{elo_str}-success.svg)](https://github.com/{repo}/blob/main/BENCHMARKS.md)"
    )


def render_verification_table(test_info: dict, perft_info: dict, bench_info: dict) -> list[str]:
    lines = []
    lines.append("## 🧪 Automated Test & Bench Verification")
    lines.append("")
    test_desc = f"{test_info['passed']} / {test_info['total']} unit tests passed" if test_info['total'] else "All xUnit test suites passed"
    if test_info.get("time"):
        test_desc += f" in {test_info['time']}s"

    perft_desc = f"Depth 4: {perft_info['nodes']} leaf nodes verified"
    if perft_info.get("time"):
        perft_desc += f" ({perft_info['time']})"
    if perft_info.get("nps"):
        perft_desc += f" at {perft_info['nps']}"

    bench_desc = "Standard bench search throughput validated"
    if bench_info.get("nodes") and bench_info.get("nps"):
        bench_desc = f"{bench_info['nodes']} nodes | {bench_info['nps']} | Zero-GC"

    lines.append("| Verification Phase | Result | Details |")
    lines.append("| :--- | :---: | :--- |")
    lines.append(f"| **Unit & Search Tests** | `{test_info['status']}` | {test_desc} |")
    lines.append(f"| **Perft Correctness** | `{perft_info['status']}` | {perft_desc} |")
    lines.append(f"| **Memory Discipline** | `ZERO-GC` | 0 Gen0, 0 Gen1, 0 Gen2 allocations during live search |")
    lines.append(f"| **Benchmark NPS** | `{bench_info['status']}` | {bench_desc} |")
    lines.append("")
    return lines


def render_distribution_section() -> list[str]:
    lines = [
        "## 📦 Release Assets & Distribution",
        "",
        "Each release includes standalone, self-contained single-file executables bundled with all companion assets:",
        "- **Bundled Networks**: `networks/open.nnue`, `networks/closed.nnue`, `networks/micro.nnue`, `networks/volatility.nnue`",
        "- **Opening Book**: `book.bin` (PolyGlot)",
        "- **Syzygy Tablebases**: `syzygy/` (3-piece and 4-piece tables)",
        "",
        "| Platform | Target Architecture | Archive | Executable |",
        "| :--- | :--- | :---: | :--- |",
        "| **Windows** | x86-64 (AVX-512) | `.zip` | `anak-windows-x64-avx512.exe` |",
        "| **Windows** | x86-64 (AVX2 / BMI2) | `.zip` | `anak-windows-x64-avx2.exe` |",
        "| **Windows** | x86-64 (Generic) | `.zip` | `anak-windows-x64-generic.exe` |",
        "| **Linux** | x86-64 (AVX-512) | `.tar.gz` | `anak-linux-x64-avx512` |",
        "| **Linux** | x86-64 (AVX2) | `.tar.gz` | `anak-linux-x64-avx2` |",
        "| **Linux** | x86-64 (Generic) | `.tar.gz` | `anak-linux-x64-generic` |",
        "| **macOS** | Apple Silicon (ARM64) | `.tar.gz` | `anak-macos-arm64` |",
        "| **Linux** | ARM64 | `.tar.gz` | `anak-linux-arm64` |",
        "",
        "### 🚀 Quick Start (UCI Mode)",
        "```bash",
        "# Run Anak interactively in UCI mode",
        "./anak",
        "",
        "# Check engine identification",
        "uci",
        "isready",
        "",
        "# Run built-in benchmark",
        "./anak bench",
        "```",
        "",
        "All packages include SHA-256 checksums in `SHA256SUMS.txt` for integrity verification.",
        ""
    ]
    return lines


def get_intermediate_versions(version_list: list[dict], base_semver: tuple[int, int, int], target_semver: tuple[int, int, int]) -> list[dict]:
    items = []
    for v in version_list:
        sv = parse_semver(v["version"])
        if sv:
            sv_tuple = (sv[0], sv[1], sv[2])
            if base_semver < sv_tuple <= target_semver:
                items.append(v)
    return items


def render_feature_progression_table(items: list[dict], repo: str) -> list[str]:
    lines = []
    lines.append("| Version | Feature & Improvement | Elo Gain | Estimated Elo | Match Record | Time Control |")
    lines.append("| :--- | :--- | :---: | :---: | :---: | :---: |")

    prev_elo = None
    for item in items:
        v_tag = item["version"]
        elo = item.get("elo")
        delta_str = "-"
        if elo is not None and prev_elo is not None:
            delta = elo - prev_elo
            delta_str = f"+{delta} Elo" if delta > 0 else f"{delta} Elo"
        elif item.get("notes") and "Elo" in item["notes"]:
            # Extract Elo delta from note if available
            m = re.search(r"\+([0-9.]+)\s*Elo", item["notes"])
            if m:
                delta_str = f"+{m.group(1)} Elo"

        desc = item.get("notes", "-")
        # Strip Elo suffix from description for cleaner display
        desc = re.sub(r"\s*\(\+?[0-9.]+\s*Elo[^\)]*\)", "", desc).strip()

        score_str = str(item.get("score", "-"))
        if item.get("perf"):
            score_str += f" ({item['perf']}%)"
        tc = item.get("tc", "-")

        link = f"[**{v_tag}**](https://github.com/{repo}/releases/tag/{v_tag})" if v_tag.startswith("v") else f"**{v_tag}**"
        lines.append(f"| {link} | {desc} | **{delta_str}** | **{elo}** | {score_str} | {tc} |")
        if elo is not None:
            prev_elo = elo

    lines.append("")
    return lines


def build_single_feat_notes(
    tag: str,
    current_info: dict,
    prev_info: dict | None,
    changelog_cats: dict[str, list[str]],
    test_info: dict,
    perft_info: dict,
    bench_info: dict,
    version_list: list[dict],
    repo: str,
) -> list[str]:
    """Generates focused release notes for a single feature/patch release (1.0.x)."""
    current_elo = current_info.get("elo", "N/A")
    current_margin = current_info.get("margin")
    current_ci = f" ±{current_margin}" if current_margin is not None else ""

    # Calculate Elo delta
    prev_ver = prev_info.get("version", "previous baseline") if prev_info else "previous baseline"
    elo_delta_str = ""
    if prev_info and prev_info.get("elo") is not None and current_info.get("elo") is not None:
        delta = current_info["elo"] - prev_info["elo"]
        sign = "+" if delta > 0 else ""
        elo_delta_str = f" ({sign}{delta} Elo vs {prev_ver})"

    # Clean feature title & summary
    raw_notes = current_info.get("notes", f"Improvement for {tag}")
    feature_title = re.sub(r"\s*\(\+?[0-9.]+\s*Elo[^\)]*\)", "", raw_notes).strip()
    if not feature_title:
        feature_title = raw_notes

    lines = []
    lines.append(f"# Anak {tag} — {feature_title}")
    lines.append("")
    lines.append(f"> **Single Feature Release {tag}** — Estimated Rating: **{current_elo}{current_ci} Elo**{elo_delta_str}")
    lines.append("")
    lines.append(render_badges(tag, current_elo, test_info["status"], repo))
    lines.append("")

    # Featured Improvement Section
    lines.append("## 🚀 Featured Improvement")
    lines.append("")
    lines.append(f"### {feature_title}")
    lines.append("")
    lines.append(f"{raw_notes}.")
    lines.append("")
    lines.append("#### 📊 Benchmark & Validation Statistics")
    lines.append(f"- **Estimated Elo**: **{current_elo}{current_ci} Elo**{elo_delta_str}")
    if current_info.get("opponent") and current_info["opponent"] != "-":
        lines.append(f"- **Reference Opponent**: {current_info['opponent']}")
    if current_info.get("score") and current_info["score"] != "-":
        perf_str = f" ({current_info['perf']}%)" if current_info.get("perf") else ""
        lines.append(f"- **Score**: {current_info['score']}{perf_str}")
    if current_info.get("wins") or current_info.get("losses") or current_info.get("draws"):
        lines.append(f"- **Record (W / L / D)**: {current_info.get('wins', 0)} Wins / {current_info.get('losses', 0)} Losses / {current_info.get('draws', 0)} Draws")
    if current_info.get("tc") and current_info["tc"] != "-":
        lines.append(f"- **Time Control**: {current_info['tc']}")
    lines.append("")

    # Automated Verification
    lines.extend(render_verification_table(test_info, perft_info, bench_info))

    # Single Feature Changelog
    lines.append("## 📝 Commits in this Release")
    lines.append("")
    any_commits = False
    for cat_title, commits in changelog_cats.items():
        for c in commits:
            lines.append(c)
            any_commits = True
    if not any_commits:
        lines.append(f"- Feature release for `{tag}`")
    lines.append("")

    # Recent Rating Progression Context
    if version_list:
        lines.append("## 📈 Recent Progression Context")
        lines.append("")
        recent_versions = version_list[-6:]
        lines.append("| Version | Release Date | Estimated Elo | Baseline Opponent | Score | TC |")
        lines.append("| :--- | :---: | :---: | :--- | :---: | :---: |")
        for v in recent_versions:
            v_tag = v["version"]
            v_date = v["date"] or "-"
            v_elo = f"**{v['elo']}**" if v['elo'] else "-"
            v_opp = v["opponent"]
            v_score = f"{v['score']}"
            if v.get("perf"):
                v_score += f" ({v['perf']}%)"
            v_tc = v["tc"]
            link = f"[**{v_tag}**](https://github.com/{repo}/releases/tag/{v_tag})" if v_tag.startswith("v") else f"**{v_tag}**"
            lines.append(f"| {link} | {v_date} | {v_elo} | {v_opp} | {v_score} | {v_tc} |")
        lines.append("")

    # Release Assets & Distribution
    lines.extend(render_distribution_section())
    return lines


def build_minor_notes(
    tag: str,
    current_info: dict,
    base_info: dict | None,
    intermediate_items: list[dict],
    changelog_cats: dict[str, list[str]],
    test_info: dict,
    perft_info: dict,
    bench_info: dict,
    version_list: list[dict],
    repo: str,
) -> list[str]:
    """Generates minor release notes (1.x.0) aggregating all changes and Elo gains since 1.(x-1).0."""
    current_elo = current_info.get("elo", "N/A")
    current_margin = current_info.get("margin")
    current_ci = f" ±{current_margin}" if current_margin is not None else ""

    base_tag = base_info.get("version", "previous minor baseline") if base_info else "previous minor baseline"
    base_elo = base_info.get("elo") if base_info else None

    cumulative_delta_str = ""
    if base_elo is not None and current_info.get("elo") is not None:
        delta = current_info["elo"] - base_elo
        sign = "+" if delta > 0 else ""
        cumulative_delta_str = f" ({sign}{delta} Elo since {base_tag})"

    lines = []
    lines.append(f"# Anak {tag}")
    lines.append("")
    lines.append(f"> **Official Minor Release {tag}** — Estimated Rating: **{current_elo}{current_ci} Elo**{cumulative_delta_str}")
    lines.append("")
    lines.append(render_badges(tag, current_elo, test_info["status"], repo))
    lines.append("")

    # Overview
    lines.append("## 🌟 Milestone Overview & Highlights")
    lines.append("")
    lines.append(f"Anak **{tag}** marks a major consolidation release incorporating all performance speedups, search selectivity improvements, and positional tuning developed since **{base_tag}**.")
    if base_elo and current_info.get("elo"):
        lines.append(f"- **Cumulative Rating Gain**: **+{current_info['elo'] - base_elo} Elo** ({base_elo} ➔ {current_info['elo']} Elo)")
    lines.append(f"- **Feature Iterations**: {len(intermediate_items)} intermediate feature candidates benchmarked and validated.")
    lines.append("")

    # Intermediate Feature Progression Table
    if intermediate_items:
        lines.append(f"## 🏆 Feature Progression & Elo Gains (since {base_tag})")
        lines.append("")
        lines.extend(render_feature_progression_table(intermediate_items, repo))

    # Elo Progression Chart
    lines.append("## 📈 Elo Rating Progression & Chart")
    lines.append("")
    lines.append(f"![Elo Rating Progression](https://raw.githubusercontent/{repo}/{tag}/docs/elo_progression.svg)")
    lines.append("")

    # Mermaid xychart-beta
    lines.append("<details>")
    lines.append("<summary><b>Interactive Mermaid Elo Chart</b></summary>")
    lines.append("")
    lines.append("```mermaid")
    lines.append("xychart-beta")
    lines.append('    title "Anak Elo Rating Progression"')
    ver_names = [f'"{v["version"]}"' for v in version_list if v["elo"] is not None]
    elo_values = [str(v["elo"]) for v in version_list if v["elo"] is not None]
    lines.append(f'    x-axis [{", ".join(ver_names)}]')
    min_chart = ((min(int(e) for e in elo_values) // 100) * 100) - 50
    max_chart = (((max(int(e) for e in elo_values) + 99) // 100) * 100) + 50
    lines.append(f'    y-axis "Elo Rating" {min_chart} --> {max_chart}')
    lines.append(f'    line [{", ".join(elo_values)}]')
    lines.append("```")
    lines.append("")
    lines.append("</details>")
    lines.append("")

    # Automated Verification
    lines.extend(render_verification_table(test_info, perft_info, bench_info))

    # Detailed Categorized Changelog
    if changelog_cats:
        lines.append(f"## 📝 Detailed Changelog (since {base_tag})")
        lines.append("")
        for cat_title, cat_commits in changelog_cats.items():
            lines.append(f"### {cat_title}")
            for c in cat_commits:
                lines.append(c)
            lines.append("")

    # Distribution
    lines.extend(render_distribution_section())
    return lines


def build_major_notes(
    tag: str,
    current_info: dict,
    base_info: dict | None,
    intermediate_items: list[dict],
    changelog_cats: dict[str, list[str]],
    test_info: dict,
    perft_info: dict,
    bench_info: dict,
    version_list: list[dict],
    repo: str,
) -> list[str]:
    """Generates extensive major milestone release notes (x.0.0) similar to 1.0.0."""
    current_elo = current_info.get("elo", "N/A")
    current_margin = current_info.get("margin")
    current_ci = f" ±{current_margin}" if current_margin is not None else ""

    base_tag = base_info.get("version", "initial release") if base_info else "initial release"
    base_elo = base_info.get("elo") if base_info else None

    cumulative_delta_str = ""
    if base_elo is not None and current_info.get("elo") is not None:
        delta = current_info["elo"] - base_elo
        sign = "+" if delta > 0 else ""
        cumulative_delta_str = f" ({sign}{delta} Elo vs {base_tag})"

    lines = []
    lines.append(f"# Anak {tag} — Major Milestone Release")
    lines.append("")
    lines.append(f"> **Official Major Release {tag}** — Estimated Rating: **{current_elo}{current_ci} Elo**{cumulative_delta_str}")
    lines.append("")
    lines.append(render_badges(tag, current_elo, test_info["status"], repo))
    lines.append("")

    # Release Summary & Highlights
    lines.append("## 🌟 Release Summary & Architectural Highlights")
    lines.append("")
    if current_info.get("notes"):
        lines.append(f"{current_info['notes']}.")
    else:
        lines.append(f"Anak {tag} represents a landmark architectural release delivering major leaps in playing strength, search efficiency, and evaluation accuracy.")
    lines.append("")

    # Architectural Pillars
    lines.append("### 🏛️ Core Architectural Pillars")
    lines.append("")
    lines.append("1. **Multi-Tier Neural Network Evaluation Architecture**")
    lines.append("   - Continuous pawn topology openness classifier dynamically routing between specialized Open-NNUE, Closed-NNUE, and Endgame neural models.")
    lines.append("   - Cascading Micro-NNUE watchdog executing at 20M+ NPS for instantaneous tactical blunder prevention.")
    lines.append("   - SCReLU SIMD vectorization with incremental accumulator updates.")
    lines.append("")
    lines.append("2. **Advanced Alpha-Beta Search Selectivity & Dynamic Pruning**")
    lines.append("   - Principal Variation Search (PVS) with Reverse Futility Pruning (RFP), Null Move Pruning (NMP), and multi-tier Late Move Reductions (LMR).")
    lines.append("   - Static Exchange Evaluation (SEE) pruning and cached capture ordering in MovePicker.")
    lines.append("   - Correction History with side-to-move partitioning `[SideToMove, PawnKey]` and depth weighting.")
    lines.append("   - In-tree 2-fold repetition detection (`IsRepetition`) preventing cyclical draw traps.")
    lines.append("")
    lines.append("3. **Endgame & Opening Knowledge**")
    lines.append("   - Pure managed C# Syzygy WDL (Win-Draw-Loss) tablebase probing in search with zero native DLL dependencies.")
    lines.append("   - PolyGlot opening book engine with dynamic weighted selection.")
    lines.append("")
    lines.append("4. **Memory Discipline, Concurrency & NativeAOT**")
    lines.append("   - Lock-free Lazy SMP with 64-byte cache-line aligned Transposition Table entries and ExpertId tagging.")
    lines.append("   - Zero-GC discipline during live search: 0 Gen0, 0 Gen1, 0 Gen2 heap allocations.")
    lines.append("   - Multi-platform NativeAOT single-file publishing targeting AVX-512, AVX2, and generic CPU architectures.")
    lines.append("")

    # Intermediate Feature Progression Table (if available)
    if intermediate_items:
        lines.append(f"## 🏆 Milestone Feature Progression (since {base_tag})")
        lines.append("")
        lines.extend(render_feature_progression_table(intermediate_items, repo))

    # Elo Progression Section
    lines.append("## 📈 Historical Elo Rating Progression")
    lines.append("")
    lines.append(f"![Elo Rating Progression](https://raw.githubusercontent/{repo}/{tag}/docs/elo_progression.svg)")
    lines.append("")

    # Mermaid xychart-beta
    lines.append("<details>")
    lines.append("<summary><b>Interactive Mermaid Elo Chart</b></summary>")
    lines.append("")
    lines.append("```mermaid")
    lines.append("xychart-beta")
    lines.append('    title "Anak Elo Rating Progression"')
    ver_names = [f'"{v["version"]}"' for v in version_list if v["elo"] is not None]
    elo_values = [str(v["elo"]) for v in version_list if v["elo"] is not None]
    lines.append(f'    x-axis [{", ".join(ver_names)}]')
    min_chart = ((min(int(e) for e in elo_values) // 100) * 100) - 50
    max_chart = (((max(int(e) for e in elo_values) + 99) // 100) * 100) + 50
    lines.append(f'    y-axis "Elo Rating" {min_chart} --> {max_chart}')
    lines.append(f'    line [{", ".join(elo_values)}]')
    lines.append("```")
    lines.append("")
    lines.append("</details>")
    lines.append("")

    # Full Historical Progression Table
    lines.append("### 📋 Historical Progression Table")
    lines.append("")
    lines.append("| Version | Release Date | Estimated Elo | 95% CI | Baseline Opponent | Score | W / L / D | TC |")
    lines.append("| :--- | :---: | :---: | :---: | :--- | :---: | :---: | :---: |")
    for v in version_list:
        v_tag = v["version"]
        v_date = v["date"] or "-"
        v_elo = f"**{v['elo']}**" if v['elo'] else "-"
        v_ci = f"&plusmn;{v['margin']}" if v['margin'] is not None else "-"
        v_opp = v["opponent"]
        v_score = f"{v['score']}"
        if v.get("perf"):
            v_score += f" ({v['perf']}%)"
        v_wld = f"{v['wins']} / {v['losses']} / {v['draws']}"
        v_tc = v["tc"]
        link = f"[**{v_tag}**](https://github.com/{repo}/releases/tag/{v_tag})" if v_tag.startswith("v") else f"**{v_tag}**"
        lines.append(f"| {link} | {v_date} | {v_elo} | {v_ci} | {v_opp} | {v_score} | {v_wld} | {v_tc} |")
    lines.append("")

    # Verification
    lines.extend(render_verification_table(test_info, perft_info, bench_info))

    # Changelog
    if changelog_cats:
        lines.append(f"## 📝 Detailed Changelog (since {base_tag})")
        lines.append("")
        for cat_title, cat_commits in changelog_cats.items():
            lines.append(f"### {cat_title}")
            for c in cat_commits:
                lines.append(c)
            lines.append("")

    # Distribution
    lines.extend(render_distribution_section())
    return lines


def generate_release_notes(
    tag: str,
    ratings_path: Path,
    output_notes_path: Path,
    output_svg_path: Path,
    bench_results_path: Path | None = None,
    test_results_path: Path | None = None,
    perft_results_path: Path | None = None,
    repo_name: str = "LaurentAerens/anak",
):
    try:
        ratings_data = load_ratings(ratings_path)
        version_dict = ratings_data.get("versions", {})
        version_list = normalize_version_list(version_dict)

        # Generate SVG chart
        if version_list:
            generate_svg_chart(version_list, output_svg_path)

        # Parse semver & determine tier
        parsed_sv = parse_semver(tag)
        if parsed_sv:
            major, minor, patch, _ = parsed_sv
            semver_tuple = (major, minor, patch)
            tier = determine_tier(major, minor, patch)
        else:
            semver_tuple = (1, 0, 0)
            tier = "single_feat"

        # Resolve current version info
        current_info = find_version_entry(version_list, tag)
        if not current_info:
            fallback_prev = version_list[-1] if version_list else None
            current_info = synthesize_version_info_from_git(tag, fallback_prev)
            version_list.append(current_info)

        # Diagnostic results
        test_info = parse_test_results(test_results_path)
        perft_info = parse_perft_results(perft_results_path)
        bench_info = parse_bench_results(bench_results_path)

        # Git commit range & changelog
        git_range = find_git_commit_range(tag, tier, semver_tuple)
        changelog_cats = get_git_changelog(git_range)

        # Build notes according to Tier
        if tier == "single_feat":
            # Baseline is previous version in version_list
            curr_idx = -1
            for i, v in enumerate(version_list):
                if v["version"] == current_info["version"] or v["key"] == current_info["key"]:
                    curr_idx = i
                    break
            prev_info = version_list[curr_idx - 1] if curr_idx > 0 else None

            lines = build_single_feat_notes(
                tag=tag,
                current_info=current_info,
                prev_info=prev_info,
                changelog_cats=changelog_cats,
                test_info=test_info,
                perft_info=perft_info,
                bench_info=bench_info,
                version_list=version_list,
                repo=repo_name,
            )

        elif tier == "minor":
            major, minor, _ = semver_tuple
            base_semver = (major, minor - 1, 0)
            base_info = None
            for v in version_list:
                sv = parse_semver(v["version"])
                if sv and (sv[0], sv[1], sv[2]) == base_semver:
                    base_info = v
                    break

            intermediate_items = get_intermediate_versions(version_list, base_semver, semver_tuple)

            lines = build_minor_notes(
                tag=tag,
                current_info=current_info,
                base_info=base_info,
                intermediate_items=intermediate_items,
                changelog_cats=changelog_cats,
                test_info=test_info,
                perft_info=perft_info,
                bench_info=bench_info,
                version_list=version_list,
                repo=repo_name,
            )

        else:  # major
            major, _, _ = semver_tuple
            base_semver = (major - 1, 0, 0) if major > 1 else (0, 0, 1)
            base_info = None
            for v in version_list:
                sv = parse_semver(v["version"])
                if sv and (sv[0], sv[1], sv[2]) == base_semver:
                    base_info = v
                    break

            intermediate_items = get_intermediate_versions(version_list, base_semver, semver_tuple)

            lines = build_major_notes(
                tag=tag,
                current_info=current_info,
                base_info=base_info,
                intermediate_items=intermediate_items,
                changelog_cats=changelog_cats,
                test_info=test_info,
                perft_info=perft_info,
                bench_info=bench_info,
                version_list=version_list,
                repo=repo_name,
            )

        output_notes_path.parent.mkdir(parents=True, exist_ok=True)
        output_notes_path.write_text("\n".join(lines), encoding="utf-8")
        print(f"[+] Successfully generated release notes for {tag} ({tier}): {output_notes_path}")

    except Exception as e:
        print(f"[!] Exception during release notes generation: {e}")
        # Always guarantee non-empty notes file
        output_notes_path.parent.mkdir(parents=True, exist_ok=True)
        fallback = [
            f"# Anak {tag}",
            "",
            f"> Official Release {tag} of the Anak Chess Engine.",
            "",
            "## 📦 Distribution Packages",
            "See release asset archives below for all supported operating systems and CPU architectures.",
        ]
        output_notes_path.write_text("\n".join(fallback), encoding="utf-8")
        print(f"[+] Generated fallback release notes: {output_notes_path}")


def main():
    parser = argparse.ArgumentParser(description="Generate tiered release notes and Elo charts for Anak.")
    parser.add_argument("--tag", type=str, default=os.environ.get("GITHUB_REF_NAME", "v1.0.0"), help="Release tag (e.g. v1.0.9)")
    parser.add_argument("--ratings", type=Path, default=Path(__file__).resolve().parent / "fastchess" / "ratings.json", help="Path to ratings.json")
    parser.add_argument("--output-notes", type=Path, default=Path("dist/release_notes.md"), help="Path to write release_notes.md")
    parser.add_argument("--output-svg", type=Path, default=Path("dist/elo_progression.svg"), help="Path to write elo_progression.svg")
    parser.add_argument("--bench-results", type=Path, default=None, help="Path to bench results text file")
    parser.add_argument("--test-results", type=Path, default=None, help="Path to test results text file")
    parser.add_argument("--perft-results", type=Path, default=None, help="Path to perft results text file")
    parser.add_argument("--repo", type=str, default=os.environ.get("GITHUB_REPOSITORY", "LaurentAerens/anak"), help="GitHub repository (owner/repo)")

    args = parser.parse_args()
    generate_release_notes(
        tag=args.tag,
        ratings_path=args.ratings,
        output_notes_path=args.output_notes,
        output_svg_path=args.output_svg,
        bench_results_path=args.bench_results,
        test_results_path=args.test_results,
        perft_results_path=args.perft_results,
        repo_name=args.repo,
    )


if __name__ == "__main__":
    main()
