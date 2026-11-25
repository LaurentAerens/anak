#!/usr/bin/env python3
"""Turn real Anak/Zanak principal variations or visited search graphs into a movie."""

import argparse
import asyncio
from bisect import bisect_right
from collections import Counter, deque
from collections.abc import Iterable
from contextlib import contextmanager, ExitStack
from dataclasses import asdict, dataclass
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import tempfile

import chess
import chess.engine
import imageio_ffmpeg
from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[2]
WIDTH, HEIGHT = 1600, 900
BG = "#0b1020"
PANEL = "#121b30"
TEXT = "#eef3ff"
MUTED = "#91a2bc"
COLORS = ("#55e6c1", "#ffba69")
MAX_REPORTS = 12
TREE_PLIES = 6


@dataclass(frozen=True)
class Report:
    depth: int
    nodes: int
    seconds: float
    cp: int | None
    mate: int | None
    bound: str
    pv: tuple[str, ...]


@dataclass
class TraceEdge:
    source: str
    target: str
    move: str
    visits: int = 1


@dataclass
class SearchGraph:
    root: str
    edges: list[TraceEdge]
    events: list[int] | None = None
    event_seconds: list[float] | None = None
    elapsed_seconds: float | None = None


def validate_graph_timing(graph: SearchGraph) -> None:
    if graph.event_seconds is None and graph.elapsed_seconds is None:
        return
    if graph.events is None or graph.event_seconds is None or graph.elapsed_seconds is None:
        raise ValueError("Timed search traces need chronological events, event times and an end time.")
    if not isinstance(graph.event_seconds, list):
        raise ValueError("Search trace timestamps must be a list.")
    if len(graph.event_seconds) != len(graph.events):
        raise ValueError("Search trace needs one timestamp per visit.")
    times = [graph.elapsed_seconds, *graph.event_seconds]
    if any(type(value) not in (int, float) or not math.isfinite(value) or value < 0 for value in times):
        raise ValueError("Search trace times must be finite and non-negative.")
    if any(a > b for a, b in zip(graph.event_seconds, graph.event_seconds[1:])):
        raise ValueError("Search trace timestamps must be chronological.")
    if graph.event_seconds and graph.event_seconds[-1] > graph.elapsed_seconds:
        raise ValueError("Search trace visits cannot occur after the search ended.")


def graph_events(graph: SearchGraph) -> list[int]:
    if graph.events is None:
        return list(range(len(graph.edges)))
    if any(type(index) is not int or not 0 <= index < len(graph.edges) for index in graph.events):
        raise ValueError("Search trace events must reference existing edge indices.")
    counts = Counter(graph.events)
    if any(counts[index] != edge.visits for index, edge in enumerate(graph.edges)):
        raise ValueError("Search trace events do not match recorded visit counts.")
    discovered = {graph.root}
    for index in graph.events:
        edge = graph.edges[index]
        if edge.source not in discovered:
            raise ValueError("Search trace event references a position before it was discovered.")
        discovered.add(edge.target)
    return graph.events


def graph_depths(graph: SearchGraph) -> dict[str, int]:
    children: dict[str, list[str]] = {}
    for edge in graph.edges:
        children.setdefault(edge.source, []).append(edge.target)
    depths = {graph.root: 0}
    queue = deque([graph.root])
    while queue:
        parent = queue.popleft()
        for child in children.get(parent, []):
            if child not in depths:
                depths[child] = depths[parent] + 1
                queue.append(child)
    if any(edge.source not in depths or edge.target not in depths for edge in graph.edges):
        raise ValueError("Search trace contains edges disconnected from the root; capture instrumentation is incomplete.")
    return depths


def graph_from_records(
    root: str, records: Iterable[dict], events: list[int] | None = None, record_events: bool = False,
    event_seconds: list[float] | None = None, elapsed_seconds: float | None = None,
) -> SearchGraph:
    def key(value: str) -> str:
        if not isinstance(value, str) or len(value) != 16 or any(c not in "0123456789ABCDEF" for c in value):
            raise ValueError("Search trace position keys must be 16 uppercase hexadecimal digits.")
        return value

    graph = SearchGraph(key(root), [], [] if record_events else None)
    seen: dict[tuple[str, str, str], int] = {}
    indices = []
    for record in records:
        source, target, move = key(record["source"]), key(record["target"]), record["move"]
        if not isinstance(move, str) or chess.Move.from_uci(move).uci() != move:
            raise ValueError("Invalid move in search trace.")
        visits = record.get("visits", 1)
        if type(visits) is not int or visits < 1:
            raise ValueError("Search trace visits must be positive integers.")
        identity = (source, target, move)
        if identity in seen:
            index = seen[identity]
            graph.edges[index].visits += visits
        else:
            index = len(graph.edges)
            graph.edges.append(TraceEdge(source, target, move, visits))
            seen[identity] = index
        indices.append(index)
        if graph.events is not None:
            graph.events.extend([index] * visits)
    if events is not None:
        if not isinstance(events, list) or any(type(i) is not int or not 0 <= i < len(indices) for i in events):
            raise ValueError("Search trace events must reference existing edge indices.")
        graph.events = [indices[i] for i in events]
    graph.event_seconds = event_seconds
    graph.elapsed_seconds = elapsed_seconds
    graph_depths(graph)
    graph_events(graph)
    validate_graph_timing(graph)
    return graph


def load_trace(path: Path) -> SearchGraph:
    with path.open(encoding="utf-8-sig") as stream:
        header = json.loads(stream.readline())
        times = []
        timed = None
        elapsed = None
        ended = False

        def records():
            nonlocal timed, elapsed, ended
            for line in stream:
                record = json.loads(line)
                if ended:
                    raise ValueError("Search trace contains records after the search ended.")
                if "elapsed_seconds" in record:
                    elapsed, ended = record["elapsed_seconds"], True
                    continue
                has_time = "seconds" in record
                if timed is not None and timed != has_time:
                    raise ValueError("Search trace mixes timed and untimed visits.")
                timed = has_time
                if has_time:
                    times.append(record["seconds"])
                yield {"source": record["from"], "target": record["to"], "move": record["move"]}

        graph = graph_from_records(header["root"], records(), record_events=True)
        if timed and not ended:
            raise ValueError("Timed search trace has no end record; capture is incomplete.")
        if ended:
            if timed is False:
                raise ValueError("Search trace has an end time but untimed visits.")
            graph.event_seconds, graph.elapsed_seconds = times, elapsed
            validate_graph_timing(graph)
        return graph


@dataclass
class Search:
    name: str
    bestmove: str
    reports: list[Report]
    limit_depth: int | None = None
    limit_seconds: float | None = None
    graph: SearchGraph | None = None
    threads: int | None = None


def position(fen: str) -> chess.Board:
    board = chess.Board() if fen.lower() == "startpos" else chess.Board(fen)
    if not board.is_valid():
        raise ValueError("The FEN is not a valid chess position.")
    if board.is_game_over():
        raise ValueError("Choose a non-terminal position with moves to search.")
    return board


def legal_line(board: chess.Board, moves: tuple[str, ...]) -> list[str]:
    board = board.copy(stack=False)
    labels = []
    for token in moves:
        move = chess.Move.from_uci(token)
        if move not in board.legal_moves:
            raise ValueError(f"Engine reported illegal PV move {token} after {labels}.")
        labels.append(board.san(move))
        board.push(move)
    return labels


def rebuild_engines() -> None:
    for project in ("Anak.Protocol", "Zanak"):
        print(f"Rebuilding {project} from current sources (Release)...", flush=True)
        result = subprocess.run(
            ["dotnet", "build", str(ROOT / "src" / project), "-c", "Release", "-t:Rebuild"],
            cwd=str(ROOT),
        )
        if result.returncode:
            raise RuntimeError(f"{project} rebuild failed (exit {result.returncode}); capture aborted.")


def engine_command(value: str | None, project: str, name: str) -> list[str]:
    if value:
        target = Path(value).expanduser().resolve()
    else:
        candidates = sorted(
            (ROOT / "src" / project / "bin" / "Release").glob(f"net*/{name}.dll"),
            key=lambda path: path.stat().st_mtime,
            reverse=True,
        )
        if not candidates:
            raise FileNotFoundError(
                f"Build {project} first: dotnet build src\\{project} -c Release"
            )
        target = candidates[0]
    if not target.is_file():
        raise FileNotFoundError(f"Engine not found: {target}")
    return ["dotnet", str(target)] if target.suffix.lower() == ".dll" else [str(target)]


async def capture(
    name: str, command: list[str], board: chess.Board, depth: int | None, threads: int,
    seconds: float | None = None, all_lines: bool = False,
) -> Search:
    if all_lines and threads != 1:
        raise ValueError("--all-lines requires --threads 1 to keep exploration followable.")
    transport, engine = await chess.engine.popen_uci(command, cwd=str(ROOT))
    empty_tables = tempfile.TemporaryDirectory(prefix="anak-movie-tables-")
    try:
        options = {
            "Threads": threads,
            "Hash": 64,
            "OwnBook": False,
            "NnuePath": str(ROOT / "networks"),
            # Empty strings auto-discover the repository's tables; use an empty directory.
            "SyzygyPath": empty_tables.name,
        }
        trace_path = Path(empty_tables.name) / "search-trace.jsonl"
        if all_lines:
            if "SearchTraceFile" not in engine.options:
                raise RuntimeError(f"{name} does not support SearchTraceFile; rebuild the local engines with --rebuild.")
            if "SearchAlgorithm" in engine.options:
                options["SearchAlgorithm"] = "Minimax"
            options["SearchTraceFile"] = str(trace_path)
        await engine.configure({key: value for key, value in options.items() if key in engine.options})
        await engine.ping()
        reports = []
        analysis = await engine.analysis(board, chess.engine.Limit(depth=depth, time=seconds))
        async for info in analysis:
            if "string" in info:
                print(f"{name}: {info['string']}", file=sys.stderr)
            if not info.get("pv") or "score" not in info or "depth" not in info:
                continue
            report = report_from_info(board, info)
            if not reports or report != reports[-1]:
                reports.append(report)
        result = await analysis.wait()
        if not reports:
            raise RuntimeError(f"{name} returned no scored PV reports; cannot create an honest movie.")
        if result.move is None or result.move not in board.legal_moves:
            raise RuntimeError(f"{name} returned no legal best move.")
        await engine.quit()
        graph = load_trace(trace_path) if all_lines else None
        return Search(name, result.move.uci(), reports, depth, seconds, graph, threads)
    finally:
        # Closing the subprocess transport also terminates an engine on timeout/error.
        transport.close()
        empty_tables.cleanup()


def report_from_info(board: chess.Board, info: chess.engine.InfoDict) -> Report:
    pv = tuple(move.uci() for move in info["pv"])
    legal_line(board, pv)
    score = info["score"].white()
    bound = "lower" if info.get("lowerbound") else "upper" if info.get("upperbound") else ""
    if board.turn == chess.BLACK:
        bound = {"lower": "upper", "upper": "lower", "": ""}[bound]
    return Report(
        depth=info["depth"],
        nodes=info.get("nodes", 0),
        seconds=info.get("time", 0.0),
        cp=score.score(),
        mate=score.mate(),
        bound=bound,
        pv=pv,
    )


def sample_reports(reports: list[Report], count: int = MAX_REPORTS) -> list[Report]:
    if len(reports) <= count:
        return reports
    return [reports[round(i * (len(reports) - 1) / (count - 1))] for i in range(count)]


def save_capture(path: Path, board: chess.Board, depth: int, searches: list[Search]) -> None:
    data = {
        "schema": 1,
        "view": "visited-search-graph" if all(search.graph is not None for search in searches) else "reported-principal-variations",
        "fen": board.fen(),
        "requested_depth": depth,
        "searches": [asdict(search) for search in searches],
    }
    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


def load_capture(path: Path) -> tuple[chess.Board, int, list[Search]]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if data["schema"] != 1 or data["view"] not in ("reported-principal-variations", "visited-search-graph"):
        raise ValueError("Unsupported search movie capture format.")
    board = position(data["fen"])
    depth = data["requested_depth"]
    if not isinstance(depth, int) or not 1 <= depth <= 128:
        raise ValueError("Capture depth must be between 1 and 128.")
    searches = []
    if len(data["searches"]) != 2:
        raise ValueError("A comparison capture must contain exactly two engines.")
    for item in data["searches"]:
        reports = [Report(**{**report, "pv": tuple(report["pv"])}) for report in item["reports"]]
        if not reports:
            raise ValueError("Each engine must have at least one report.")
        for report in reports:
            if not report.pv or (report.cp is None) == (report.mate is None):
                raise ValueError("Each report needs a PV and exactly one cp/mate score.")
            if report.depth < 0 or report.nodes < 0 or not math.isfinite(report.seconds) or report.seconds < 0:
                raise ValueError("Invalid depth, nodes or elapsed time in capture.")
            if report.bound not in ("", "lower", "upper"):
                raise ValueError("Invalid score bound in capture.")
            legal_line(board, report.pv)
        if chess.Move.from_uci(item["bestmove"]) not in board.legal_moves:
            raise ValueError("Capture contains an illegal best move.")
        limit_depth = item.get("limit_depth")
        limit_seconds = item.get("limit_seconds")
        if limit_depth is not None and (not isinstance(limit_depth, int) or not 1 <= limit_depth <= 128):
            raise ValueError("Capture engine depth limit must be between 1 and 128.")
        if limit_seconds is not None and (not math.isfinite(limit_seconds) or limit_seconds <= 0):
            raise ValueError("Capture engine time budget must be finite and positive.")
        graph = item.get("graph")
        if graph is not None:
            graph = graph_from_records(
                graph["root"], graph["edges"], graph.get("events"),
                event_seconds=graph.get("event_seconds"), elapsed_seconds=graph.get("elapsed_seconds"),
            )
        if data["view"] == "visited-search-graph" and graph is None:
            raise ValueError("Full-search captures require a graph for each engine.")
        threads = item.get("threads")
        if threads is not None and (type(threads) is not int or not 1 <= threads <= 128):
            raise ValueError("Capture engine threads must be between 1 and 128.")
        searches.append(Search(item["name"], item["bestmove"], reports, limit_depth, limit_seconds, graph, threads))
    return board, depth, searches


def score_text(report: Report) -> str:
    prefix = {"lower": ">=", "upper": "<="}.get(report.bound, "")
    return prefix + (f"M{report.mate:+d}" if report.mate is not None else f"{report.cp / 100:+.2f}")


def fonts() -> tuple[str, str]:
    windows = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts"
    text_paths = [windows / "segoeui.ttf", Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")]
    piece_paths = [windows / "seguisym.ttf", Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")]

    def find(paths: list[Path], fallback: str) -> str:
        for path in paths:
            if path.is_file():
                return str(path)
        try:
            ImageFont.truetype(fallback, 20)
            return fallback
        except OSError as exc:
            raise RuntimeError("Install Segoe UI Symbol or DejaVu Sans fonts to render chess pieces.") from exc

    return find(text_paths, "DejaVuSans.ttf"), find(piece_paths, "DejaVuSans.ttf")


class Movie:
    def __init__(self, board: chess.Board, depth: int, searches: list[Search]):
        self.board = board
        self.depth = depth
        self.searches = searches
        self.shown = [sample_reports(search.reports) for search in searches]
        text_font, piece_font = fonts()
        self.font = {size: ImageFont.truetype(text_font, size) for size in (14, 16, 18, 20, 24, 28, 36, 44)}
        self.piece_font = ImageFont.truetype(piece_font, 33)
        self.layouts = [self.layout(reports) for reports in self.shown]

    def layout(self, reports: list[Report]) -> dict[tuple[str, ...], tuple[float, float]]:
        children: dict[tuple[str, ...], list[tuple[str, ...]]] = {(): []}
        for report in reports:
            path: tuple[str, ...] = ()
            for move in report.pv[:TREE_PLIES]:
                child = (*path, move)
                if child not in children:
                    children[child] = []
                    children[path].append(child)
                path = child
        leaves = []
        for path, descendants in children.items():
            if not descendants:
                leaves.append(path)
        # DFS ordering keeps the subtrees contiguous, preventing crossing edges.
        ordered = []

        def walk(path: tuple[str, ...]) -> None:
            if not children[path]:
                ordered.append(path)
            for child in children[path]:
                walk(child)

        walk(())
        ys = {path: 510 + (i + 0.5) * 190 / len(leaves) for i, path in enumerate(ordered)}
        result = {}

        def place(path: tuple[str, ...]) -> float:
            y = sum(place(child) for child in children[path]) / len(children[path]) if children[path] else ys[path]
            result[path] = (52 + len(path) * 109, y)
            return y

        place(())
        return result

    def text(self, draw: ImageDraw.ImageDraw, xy: tuple, text: str, size: int = 18, fill: str = TEXT, **kwargs) -> None:
        draw.text(xy, text, font=self.font[size], fill=fill, **kwargs)

    def fitted(self, draw: ImageDraw.ImageDraw, xy: tuple, text: str, width: int, size: int = 18, fill: str = TEXT) -> None:
        while text and draw.textlength(text, font=self.font[size]) > width:
            text = text[:-4] + "..."
        self.text(draw, xy, text, size, fill)

    def draw_board(self, draw: ImageDraw.ImageDraw, board: chess.Board, x: int, y: int, move: chess.Move | None) -> None:
        cell = 34
        for rank in range(8):
            for file in range(8):
                square = chess.square(file, 7 - rank)
                left, top = x + file * cell, y + rank * cell
                fill = "#b9c9cf" if (rank + file) % 2 == 0 else "#557482"
                if move is not None and square in (move.from_square, move.to_square):
                    fill = "#b4be67"
                draw.rectangle((left, top, left + cell - 1, top + cell - 1), fill=fill)
                piece = board.piece_at(square)
                if piece:
                    glyph = chr(0x2660 - piece.piece_type)
                    draw.text(
                        (left + cell / 2, top + cell / 2 - 1), glyph, font=self.piece_font,
                        anchor="mm", fill="#faf7ed" if piece.color else "#162034",
                        stroke_width=1, stroke_fill="#162034",
                    )
        for file in range(8):
            self.text(draw, (x + file * cell + 13, y + 275), chr(97 + file), 14, MUTED)

    def state(self, index: int, progress: float) -> tuple[int, int]:
        reports = self.shown[index]
        lengths = [len(report.pv[:TREE_PLIES]) + 2 for report in reports]
        step = min(sum(lengths) - 1, int(max(0.0, progress) * sum(lengths)))
        for i, length in enumerate(lengths):
            if step < length:
                return i, min(step, len(reports[i].pv[:TREE_PLIES]))
            step -= length
        return len(reports) - 1, len(reports[-1].pv[:TREE_PLIES])

    def panel(self, draw: ImageDraw.ImageDraw, index: int, progress: float) -> None:
        x = 28 + index * 786
        color = COLORS[index]
        search, reports = self.searches[index], self.shown[index]
        current, revealed = self.state(index, progress)
        report = reports[current]
        active = report.pv[:revealed]
        board = self.board.copy(stack=False)
        for token in active:
            board.push_uci(token)
        draw.rounded_rectangle((x, 148, x + 758, 836), radius=20, fill=PANEL)
        draw.rounded_rectangle((x + 24, 174, x + 30, 212), radius=3, fill=color)
        self.text(draw, (x + 46, 170), search.name.upper(), 36)
        self.text(draw, (x + 534, 182), f"DEPTH {report.depth:02d}", 24, color)
        self.draw_board(draw, board, x + 28, 235, board.peek() if active else None)
        self.text(draw, (x + 328, 236), "EVALUATION / WHITE POV", 16, MUTED)
        self.text(draw, (x + 328, 258), score_text(report), 44, color)
        self.text(draw, (x + 328, 322), f"{report.nodes:,} nodes", 24)
        budget = f" / {search.limit_seconds:g}s budget" if search.limit_seconds is not None else ""
        self.text(draw, (x + 328, 359), f"{report.seconds:.3f}s engine time{budget}", 18, MUTED)
        self.text(draw, (x + 328, 402), "CURRENT REPORTED LINE", 14, MUTED)
        labels = legal_line(self.board, report.pv)
        for row in range(3):
            self.fitted(draw, (x + 328, 426 + row * 24), "  ".join(labels[row * 4:row * 4 + 4]), 398, 18)
        self.text(draw, (x + 28, 552), "REPORTED PV TREE", 16, MUTED)
        self.text(draw, (x + 533, 552), f"REPORT {current + 1}/{len(reports)}", 14, MUTED)

        seen: set[tuple[str, ...]] = {()}
        for previous in reports[:current]:
            seen.update(previous.pv[:ply] for ply in range(1, min(TREE_PLIES, len(previous.pv)) + 1))
        seen.update(active[:ply] for ply in range(1, len(active) + 1))
        active_paths = {active[:ply] for ply in range(len(active) + 1)}
        layout = self.layouts[index]

        def point(path: tuple[str, ...]) -> tuple[float, float]:
            px, py = layout[path]
            return x + px, py + 75

        for path in sorted(seen, key=len):
            if not path:
                continue
            highlighted = path in active_paths
            draw.line((point(path[:-1]), point(path)), fill=color if highlighted else "#344561", width=3 if highlighted else 1)
        for path in sorted(seen, key=len):
            px, py = point(path)
            highlighted = path in active_paths
            radius = 6 if highlighted else 4
            draw.ellipse((px - radius, py - radius, px + radius, py + radius), fill=color if highlighted else "#5b708d")
            label = legal_line(self.board, path)[-1] if path else "ROOT"
            self.text(draw, (px + 10, py), label, 14, TEXT if highlighted else MUTED, anchor="lm")
        # A compact white-POV evaluation history, not a measure of elapsed wall time.
        chart_x, chart_y, chart_w = x + 328, 513, 400
        points = []
        for i, previous in enumerate(reports[:current + 1]):
            value = previous.cp if previous.cp is not None else (1500 if previous.mate >= 0 else -1500)
            points.append((chart_x + i * chart_w / max(1, len(reports) - 1), chart_y - 15 * math.tanh(value / 300)))
        draw.line((chart_x, chart_y, chart_x + chart_w, chart_y), fill="#344561")
        if len(points) > 1:
            draw.line(points, fill=color, width=3)
        best = self.board.san(chess.Move.from_uci(search.bestmove)) if progress >= 1 else "searching..."
        self.text(draw, (x + 28, 799), f"BEST MOVE  {best}", 20, color)
        self.text(draw, (x + 485, 803), f"{len(search.reports)} captured reports", 14, MUTED)

    def frame(self, progress: float) -> Image.Image:
        image = Image.new("RGB", (WIDTH, HEIGHT), BG)
        draw = ImageDraw.Draw(image)
        self.text(draw, (32, 24), "TWO ENGINES. ONE POSITION.", 44)
        limits = []
        for search in self.searches:
            limit = f"{search.limit_seconds:g}s BUDGET" if search.limit_seconds is not None else f"DEPTH {search.limit_depth or self.depth}"
            if search.limit_seconds is not None and search.limit_depth is not None:
                limit += f" / DEPTH {search.limit_depth}"
            limits.append(f"{search.name.upper()} {limit}")
        self.text(draw, (34, 86), "     |     ".join([*limits, "REAL UCI PRINCIPAL VARIATIONS"]), 18, MUTED)
        for i in range(2):
            self.panel(draw, i, progress)
        self.text(draw, (32, 858), "Reported lines only, not every visited node. Up to 12 reports / 6 plies shown.", 16, MUTED)
        self.text(draw, (1568, 858), "Independent progress, not a speed comparison.", 16, MUTED, anchor="ra")
        draw.rectangle((0, HEIGHT - 5, round(WIDTH * min(1.0, progress)), HEIGHT), fill=COLORS[0])
        return image


class GraphMovie(Movie):
    """Apply ordered visits to persistent graph layers, without revealing future positions."""

    def __init__(self, board: chess.Board, depth: int, searches: list[Search], same_speed: bool = False):
        super().__init__(board, depth, searches)
        self.same_speed = same_speed
        self.elapsed_seconds = 0.0
        self.graphs = []
        self.events = []
        self.layouts = []
        self.parents = []
        self.layers = []
        self.processed = []
        self.edge_visits = []
        self.unique_counts = []
        self.discovered = []
        for search in searches:
            graph = search.graph
            if graph is None:
                raise ValueError("--all-lines needs an internal search trace, not a PV-only capture.")
            self.graphs.append(graph)
            self.events.append(graph_events(graph))
            validate_graph_timing(graph)
            if same_speed:
                if graph.event_seconds is None or graph.elapsed_seconds is None:
                    raise ValueError("--same-speed requires timed traces; capture again with --all-lines --rebuild.")
                self.elapsed_seconds = max(self.elapsed_seconds, graph.elapsed_seconds)
            depths = graph_depths(graph)
            levels: dict[int, list[str]] = {}
            for node, level in depths.items():
                levels.setdefault(level, []).append(node)
            positions = {}
            for level, nodes in levels.items():
                for row, node in enumerate(nodes):
                    positions[node] = (
                        42 + level * 620 / max(1, max(levels)),
                        326 + (row + 0.5) * 420 / len(nodes),
                    )
            self.layouts.append(positions)
            self.parents.append({})
            self.reset(len(self.graphs) - 1)

    def reset(self, index: int) -> None:
        layer = Image.new("RGBA", (758, HEIGHT), (0, 0, 0, 0))
        if index == len(self.layers):
            self.layers.append(layer)
            self.processed.append(0)
            self.edge_visits.append([0] * len(self.graphs[index].edges))
            self.unique_counts.append(0)
            self.discovered.append({self.graphs[index].root})
        else:
            self.layers[index] = layer
            self.processed[index] = 0
            self.edge_visits[index] = [0] * len(self.graphs[index].edges)
            self.unique_counts[index] = 0
            self.discovered[index] = {self.graphs[index].root}
        self.parents[index] = {}
        px, py = self.layouts[index][self.graphs[index].root]
        draw = ImageDraw.Draw(layer)
        draw.ellipse((px - 4, py - 4, px + 4, py + 4), fill=COLORS[index])
        self.text(draw, (px + 5, py + 4), "ROOT", 14, COLORS[index])

    def state(self, index: int, progress: float) -> int:
        if self.same_speed:
            if progress >= 1:
                return len(self.events[index])
            return bisect_right(self.graphs[index].event_seconds, max(0.0, progress) * self.elapsed_seconds)
        return min(len(self.events[index]), int(max(0.0, progress) * len(self.events[index])))

    def finished(self, index: int, progress: float) -> bool:
        if self.same_speed:
            return max(0.0, progress) * self.elapsed_seconds >= self.graphs[index].elapsed_seconds
        return progress >= 1

    @staticmethod
    def draw_edge(draw: ImageDraw.ImageDraw, source: tuple, target: tuple, color: str, width: int = 1) -> None:
        if source == target:
            px, py = source
            draw.arc((px - 10, py - 18, px + 10, py + 2), 0, 330, fill=color, width=width)
            return
        draw.line((source, target), fill=color, width=width)
        angle = math.atan2(target[1] - source[1], target[0] - source[0])
        for offset in (-0.5, 0.5):
            tip = (target[0] - 8 * math.cos(angle + offset), target[1] - 8 * math.sin(angle + offset))
            draw.line((target, tip), fill=color, width=width)

    def advance(self, index: int, count: int) -> None:
        if count < self.processed[index]:
            self.reset(index)
        draw = ImageDraw.Draw(self.layers[index])
        graph, positions = self.graphs[index], self.layouts[index]
        for event in self.events[index][self.processed[index]:count]:
            edge = graph.edges[event]
            if self.edge_visits[index][event] == 0:
                self.unique_counts[index] += 1
                self.draw_edge(draw, positions[edge.source], positions[edge.target], "#526681")
                for key in (edge.source, edge.target):
                    if key not in self.discovered[index]:
                        px, py = positions[key]
                        draw.ellipse((px - 3, py - 3, px + 3, py + 3), fill="#a0b2ca")
                        self.discovered[index].add(key)
                        if key == edge.target:
                            self.parents[index][key] = event
            self.edge_visits[index][event] += 1
        self.processed[index] = count

    def panel(self, image: Image.Image, index: int, progress: float) -> None:
        draw = ImageDraw.Draw(image)
        x = 28 + index * 786
        color, search, graph = COLORS[index], self.searches[index], self.graphs[index]
        count = self.state(index, progress)
        finished = self.finished(index, progress)
        self.advance(index, count)
        positions = self.layouts[index]
        final = search.reports[-1]
        draw.rounded_rectangle((x, 148, x + 758, 836), radius=20, fill=PANEL)
        draw.rounded_rectangle((x + 24, 174, x + 30, 212), radius=3, fill=color)
        self.text(draw, (x + 46, 170), search.name.upper(), 36)
        depth_label = f"DEPTH {final.depth:02d}" if finished else f"TARGET {search.limit_depth or self.depth}"
        self.text(draw, (x + 530, 182), depth_label, 24, color)
        discovered = len(self.discovered[index])
        self.text(draw, (x + 28, 230), f"{discovered:,} {'position' if discovered == 1 else 'positions'} discovered", 24, color)
        event_label = "VISIT" if graph.events is not None else "FIRST VISIT"
        self.text(draw, (x + 28, 266), f"{event_label} {count:,} / {len(self.events[index]):,}", 18, MUTED)
        self.text(draw, (x + 390, 266), f"{self.unique_counts[index]:,} / {len(graph.edges):,} edges", 18, MUTED)
        limit = f"LIMIT depth {search.limit_depth or self.depth}"
        if search.limit_seconds is not None:
            limit += f" / {search.limit_seconds:g}s max"
        if finished:
            limit += f" / returned {graph.elapsed_seconds:.3f}s" if graph.elapsed_seconds is not None else f" / last info {final.seconds:.3f}s"
        self.text(draw, (x + 28, 303), limit, 14, MUTED)
        image.paste(self.layers[index], (x, 0), self.layers[index])

        def point(key: str) -> tuple[float, float]:
            px, py = positions[key]
            return x + px, py

        active = []
        if count:
            current = self.events[index][count - 1]
            active.append(current)
            node = graph.edges[current].source
            while node != graph.root and node in self.parents[index]:
                parent = self.parents[index][node]
                if self.edge_visits[index][parent] == 0:
                    break
                active.append(parent)
                node = graph.edges[parent].source
        for event in reversed(active):
            edge = graph.edges[event]
            source, target = point(edge.source), point(edge.target)
            self.draw_edge(draw, source, target, color, 2)
            label = "null" if edge.move == "0000" else edge.move
            if self.edge_visits[index][event] > 1:
                label += f" x{self.edge_visits[index][event]}"
            self.text(draw, ((source[0] + target[0]) / 2 + 3, (source[1] + target[1]) / 2 - 9), label, 14, color)
        if count:
            edge = graph.edges[self.events[index][count - 1]]
            px, py = point(edge.target)
            draw.ellipse((px - 6, py - 6, px + 6, py + 6), fill=color)
            self.text(draw, (x + 28, 750), f"CURRENT MOVE  {'null' if edge.move == '0000' else edge.move}", 16, color)
        if not graph.edges:
            self.text(draw, (x + 90, 500), "Search returned without visiting a move edge.", 20, MUTED)
        best = self.board.san(chess.Move.from_uci(search.bestmove)) if finished else "searching..."
        self.text(draw, (x + 28, 777), f"BEST MOVE  {best}", 20, color)
        summary = f"Last info: {score_text(final)} / {final.nodes:,} nodes / {final.seconds:.3f}s" if finished else "Recorded search visits / chronological playback"
        self.text(draw, (x + 28, 807), summary, 16, MUTED)
        if finished:
            self.text(draw, (x + 560, 780), "FINISHED", 18, color)

    def frame(self, progress: float) -> Image.Image:
        image = Image.new("RGB", (WIDTH, HEIGHT), BG)
        draw = ImageDraw.Draw(image)
        self.text(draw, (32, 24), "TWO ENGINES. EVERY SEARCHED LINE.", 44)
        thread_label = "ONE THREAD PER ENGINE" if all(search.threads == 1 for search in self.searches) else "RECORDED EXPLORATION ORDER"
        clock = f"SHARED CLOCK {min(1.0, max(0.0, progress)) * self.elapsed_seconds:.3f}s" if self.same_speed else "POSITIONS STAY VISIBLE"
        self.text(draw, (34, 86), f"{thread_label} / NO BOARDS / {clock}", 18, MUTED)
        for i in range(2):
            self.panel(image, i, progress)
        self.text(draw, (32, 858), "Recorded visits build one persistent tree. Revisited branches light up again; null = pruning probe.", 16, MUTED)
        note = "Same clock; tracing adds overhead." if self.same_speed else "Diagnostic capture, not a speed comparison."
        self.text(draw, (1568, 858), note, 16, MUTED, anchor="ra")
        draw.rectangle((0, HEIGHT - 5, round(WIDTH * min(1.0, progress)), HEIGHT), fill=COLORS[0])
        return image


@contextmanager
def encoder(path: Path, fps: int):
    command = [
        imageio_ffmpeg.get_ffmpeg_exe(), "-hide_banner", "-loglevel", "error", "-y",
        "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{WIDTH}x{HEIGHT}",
        "-r", str(fps), "-i", "-", "-an",
    ]
    if path.suffix.lower() == ".gif":
        command += [
            "-filter_complex",
            "[0:v]split[a][b];[a]palettegen=stats_mode=single[p];[b][p]paletteuse=new=1",
            "-loop", "0",
        ]
    else:
        command += ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "20", "-movflags", "+faststart"]
    command.append(str(path))
    with tempfile.TemporaryFile() as errors:
        process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=errors)
        try:
            with process.stdin:
                yield process.stdin
        finally:
            try:
                returncode = process.wait(timeout=30)
            except subprocess.TimeoutExpired as exc:
                process.kill()
                process.wait()
                raise RuntimeError(f"FFmpeg did not finish encoding {path}.") from exc
            if returncode:
                errors.seek(0)
                detail = errors.read().decode("utf-8", errors="replace").strip()
                raise RuntimeError(f"FFmpeg failed for {path} (exit {returncode}): {detail}")


def frame_progress(index: int, count: int, duration: float, fps: int) -> float:
    if index == count - 1:
        return 1.0
    hold = min(2.0, duration / 4)
    return min(1.0, max(0.0, (index / fps - 0.7) / max(0.1, duration - hold - 0.7)))


def render(movie: Movie, output: Path, duration: float, fps: int, also_gif: bool) -> None:
    count = round(duration * fps)
    paths = [output]
    if also_gif and output.suffix.lower() != ".gif":
        paths.append(output.with_suffix(".gif"))
    with ExitStack() as stack:
        writers = [stack.enter_context(encoder(path, fps)) for path in paths]
        for i in range(count):
            progress = frame_progress(i, count, duration, fps)
            frame = movie.frame(progress).tobytes()
            for writer in writers:
                writer.write(frame)
    movie.frame(1.0).save(output.with_suffix(".png"))


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    result.add_argument("--fen", default="startpos", help="Quoted FEN or startpos")
    result.add_argument("--depth", type=int, help="Requested depth (1-128): default 8, or 3 with --all-lines")
    result.add_argument("--anak-seconds", type=float, default=0, help="Anak time budget alongside depth; 0 omits movetime (depth only)")
    result.add_argument("--zanak-seconds", type=float, help="Zanak time budget: default 120s, or 1s with --all-lines; 0 omits movetime")
    result.add_argument("--zanak-depth", type=int, help="Override Zanak depth independently of --depth (1-128)")
    result.add_argument("--output", type=Path, default=ROOT / "artifacts" / "search_movie" / "anak-vs-zanak.mp4")
    result.add_argument("--also-gif", action="store_true", help="Export a looping GIF alongside the MP4")
    result.add_argument("--duration", type=float, default=60, help="Movie duration in seconds (3-600)")
    result.add_argument("--fps", type=int, default=15, help="Frames per second (1-60)")
    result.add_argument("--threads", type=int, default=1, help="Search threads per engine (1-128; --all-lines requires 1)")
    result.add_argument("--timeout", type=float, default=300, help="Watchdog seconds per engine; must exceed both engine time budgets")
    result.add_argument("--anak", help="Anak executable or DLL; defaults to local Release build")
    result.add_argument("--zanak", help="Zanak executable or DLL; defaults to local Release build")
    result.add_argument("--rebuild", action="store_true", help="Rebuild both local dev engines in Release before capturing")
    result.add_argument("--replay", type=Path, help="Render a saved JSON capture without running engines")
    result.add_argument("--all-lines", action="store_true", help="Replay single-thread search visits as one growing tree, no boards")
    result.add_argument("--same-speed", action="store_true", help="Use one recorded-time scale for both trees; faster searches finish earlier")
    return result


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        if args.depth is None:
            args.depth = 3 if args.all_lines else 8
        if args.zanak_seconds is None:
            args.zanak_seconds = 1 if args.all_lines else 120
        if args.same_speed and not (args.all_lines or args.replay):
            raise ValueError("--same-speed requires --all-lines or a timed graph --replay.")
        if not 1 <= args.depth <= 128 or not 1 <= args.threads <= 128:
            raise ValueError("Depth and threads must be between 1 and 128.")
        if args.all_lines and args.threads != 1:
            raise ValueError("--all-lines requires --threads 1 to keep exploration followable.")
        if args.zanak_depth is not None and not 1 <= args.zanak_depth <= 128:
            raise ValueError("Zanak depth must be between 1 and 128.")
        for name, seconds in (("Anak", args.anak_seconds), ("Zanak", args.zanak_seconds)):
            if not math.isfinite(seconds) or seconds < 0:
                raise ValueError(f"{name} seconds must be finite and non-negative.")
            if 0 < seconds < 0.001:
                raise ValueError(f"{name} seconds must be 0 (omit movetime) or at least 0.001 (one millisecond).")
        if not 1 <= args.fps <= 60 or not 3 <= args.duration <= 600:
            raise ValueError("Use 1-60 fps and a duration between 3 and 600 seconds.")
        if not math.isfinite(args.timeout) or args.timeout <= 0:
            raise ValueError("Timeout must be finite and positive.")
        if not args.replay and args.timeout <= max(args.anak_seconds, args.zanak_seconds):
            raise ValueError("--timeout must exceed both --anak-seconds and --zanak-seconds to allow startup and search cleanup.")
        if args.output.suffix.lower() not in (".mp4", ".gif"):
            raise ValueError("Output must end in .mp4 or .gif.")
        if args.replay and args.output.with_suffix(".json").resolve() == args.replay.resolve():
            raise ValueError("Choose an output stem different from the replay capture.")
        if args.rebuild and (args.replay or args.anak or args.zanak):
            raise ValueError("--rebuild uses local dev engines; do not combine it with --replay, --anak or --zanak.")
        if args.replay:
            board, depth, searches = load_capture(args.replay)
            if args.same_speed and any(search.graph is None for search in searches):
                raise ValueError("--same-speed requires an internal search graph, not a PV-only capture.")
            for search in searches:
                if search.graph is not None and search.graph.events is None:
                    print(f"{search.name}: legacy capture has first-discovery order only; recapture for chronological revisits.", file=sys.stderr)
        else:
            board, depth = position(args.fen), args.depth
            searches = []
            if args.all_lines and max(depth, args.zanak_depth or depth) > 4:
                print(
                    "Warning: deep --all-lines searches can produce large trace files and dense graphs. "
                    "Use --anak-seconds and --zanak-seconds to bound search work.",
                    file=sys.stderr,
                )
            if args.rebuild:
                rebuild_engines()
            commands = [
                ("Anak", engine_command(args.anak, "Anak.Protocol", "anak"), depth, args.anak_seconds or None),
                (
                    "Zanak", engine_command(args.zanak, "Zanak", "zanak"),
                    args.zanak_depth or depth,
                    args.zanak_seconds or None,
                ),
            ]
            for name, command, limit_depth, seconds in commands:
                limits = []
                if limit_depth is not None:
                    limits.append(f"depth {limit_depth}")
                if seconds is not None:
                    limits.append(f"{seconds:g}s time budget")
                print(f"Capturing {name}: {', '.join(limits)}...", flush=True)
                capture_options = {"all_lines": True} if args.all_lines else {}
                search = asyncio.run(asyncio.wait_for(
                    capture(name, command, board, limit_depth, args.threads, seconds, **capture_options), args.timeout,
                ))
                searches.append(search)
                elapsed = max(report.seconds for report in search.reports)
                usage = f"{elapsed:.3f}s reported engine time"
                if seconds is not None:
                    usage += f" / {seconds:g}s budget (engine may finish early)"
                print(f"  {len(search.reports)} reports; best move {search.bestmove}; {usage}", flush=True)
                if search.graph is not None:
                    print(f"  {len(search.graph.edges):,} unique internal move edges; none sampled.", flush=True)
                    if search.graph.elapsed_seconds is not None:
                        print(f"  Search trace ended at {search.graph.elapsed_seconds:.3f}s.", flush=True)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        save_capture(args.output.with_suffix(".json"), board, depth, searches)
        print("Rendering movie...", flush=True)
        full_graph = args.all_lines or all(search.graph is not None for search in searches)
        movie = GraphMovie(board, depth, searches, same_speed=args.same_speed) if full_graph else Movie(board, depth, searches)
        render(movie, args.output, args.duration, args.fps, args.also_gif)
        print(f"Saved {args.output.resolve()}\nCapture and poster saved beside the movie.")
        return 0
    except TimeoutError:
        print(f"Error: engine exceeded --timeout {args.timeout:g}s. Reduce its search limit or raise --timeout.", file=sys.stderr)
    except (OSError, ValueError, KeyError, TypeError, RuntimeError, chess.engine.EngineError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
