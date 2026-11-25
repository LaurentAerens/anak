import asyncio
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import call, patch

import chess
import chess.engine
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
from search_movie import (
    GraphMovie, Movie, Report, Search, SearchGraph, TraceEdge,
    capture, encoder, graph_events, graph_from_records, legal_line, load_capture, load_trace, main,
    position, rebuild_engines, render, report_from_info, ROOT, sample_reports, save_capture, score_text,
    validate_graph_timing,
)


def report(pv=("e2e4", "e7e5"), depth=2):
    return Report(depth, 123, 0.1, 25, None, "", pv)


class SearchMovieTests(unittest.TestCase):
    def test_rejects_invalid_and_terminal_positions(self):
        for fen in ("not a FEN", "8/8/8/8/8/8/8/8 w - - 0 1", "7k/6Q1/6K1/8/8/8/8/8 b - - 0 1"):
            with self.subTest(fen=fen), self.assertRaises(ValueError):
                position(fen)

    def test_pv_legality_and_san_for_castling_and_promotion(self):
        self.assertEqual(legal_line(chess.Board(), ("e2e4", "e7e5")), ["e4", "e5"])
        with self.assertRaisesRegex(ValueError, "illegal PV"):
            legal_line(chess.Board(), ("e2e5",))
        board = chess.Board("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1")
        self.assertEqual(legal_line(board, ("e1g1",)), ["O-O"])
        board = chess.Board("7k/P7/8/8/8/8/8/7K w - - 0 1")
        self.assertEqual(legal_line(board, ("a7a8q",)), ["a8=Q+"])

    def test_white_pov_flips_black_score_and_bound(self):
        board = chess.Board()
        board.push_uci("e2e4")
        info = {
            "depth": 3, "score": chess.engine.PovScore(chess.engine.Cp(42), chess.BLACK),
            "lowerbound": True, "pv": [chess.Move.from_uci("e7e5")],
        }
        result = report_from_info(board, info)
        self.assertEqual(score_text(result), "<=-0.42")
        info["score"] = chess.engine.PovScore(chess.engine.Mate(3), chess.BLACK)
        self.assertEqual(score_text(report_from_info(board, info)), "<=M-3")

    def test_sampling_preserves_first_last_and_order(self):
        reports = [report(depth=i) for i in range(50)]
        selected = sample_reports(reports)
        self.assertEqual(len(selected), 12)
        self.assertIs(selected[0], reports[0])
        self.assertIs(selected[-1], reports[-1])
        self.assertEqual(selected, sorted(selected, key=lambda entry: entry.depth))

    def test_capture_round_trip_and_illegal_replay_rejection(self):
        board = chess.Board()
        searches = [
            Search("Anak", "e2e4", [report()], limit_depth=8),
            Search("Zanak", "e2e4", [report()], limit_seconds=120),
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.json"
            save_capture(path, board, 2, searches)
            replay_board, depth, replay_searches = load_capture(path)
            self.assertEqual((replay_board.fen(), depth, replay_searches), (board.fen(), 2, searches))
            data = json.loads(path.read_text())
            for item in data["searches"]:
                del item["limit_depth"]
                del item["limit_seconds"]
            path.write_text(json.dumps(data))
            self.assertIsNone(load_capture(path)[2][1].limit_seconds)
            data["searches"][0]["reports"][0]["pv"] = ["e2e5"]
            path.write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, "illegal PV"):
                load_capture(path)

    def test_real_uci_subprocess_capture_and_timeout(self):
        script = """
import json
import sys
expected = json.loads(sys.argv[1])
for line in sys.stdin:
    if line.strip() == 'uci':
        print('id name Test Engine')
        print('option name OwnBook type check default true')
        print('uciok', flush=True)
    elif line.strip() == 'isready':
        print('readyok', flush=True)
    elif line.startswith('go'):
        tokens = line.split()[1:]
        if dict(zip(tokens[::2], tokens[1::2])) == expected:
            print('info depth 1 score cp 25 nodes 9 time 10 pv e2e4 e7e5', flush=True)
        print('bestmove e2e4', flush=True)
    elif line.strip() == 'quit':
        break
"""
        for depth, seconds, expected in (
            (1, None, {"depth": "1"}),
            (None, 120, {"movetime": "120000"}),
            (16, 5, {"depth": "16", "movetime": "5000"}),
            (12, 0.025, {"depth": "12", "movetime": "25"}),
        ):
            with self.subTest(depth=depth, seconds=seconds):
                result = asyncio.run(asyncio.wait_for(capture(
                    "Test", [sys.executable, "-u", "-c", script, json.dumps(expected)],
                    chess.Board(), depth, 1, seconds,
                ), 10))
                self.assertEqual(result.bestmove, "e2e4")
                self.assertEqual(result.reports[0].pv, ("e2e4", "e7e5"))
                self.assertEqual((result.limit_depth, result.limit_seconds), (depth, seconds))
        with self.assertRaises(TimeoutError):
            asyncio.run(asyncio.wait_for(
                capture("Stalled", [sys.executable, "-u", "-c", "import time; time.sleep(20)"], chess.Board(), 1, 1), 0.3,
            ))

    def test_movie_reveals_legal_prefixes_and_exports_looping_gif(self):
        reports = [report(), report(("d2d4", "d7d5"), 3)]
        movie = Movie(chess.Board(), 3, [Search(name, "d2d4", reports) for name in ("Anak", "Zanak")])
        self.assertEqual(movie.state(0, 0), (0, 0))
        self.assertEqual(movie.state(0, 1), (1, 2))
        self.assertEqual(len(movie.layouts[0]), 5)
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "movie.gif"
            render(movie, output, duration=3, fps=2, also_gif=False)
            with Image.open(output) as gif:
                self.assertEqual(gif.size, (1600, 900))
                self.assertEqual(gif.n_frames, 6)
                self.assertEqual(gif.info["loop"], 0)
                self.assertEqual(gif.info["duration"], 500)
            self.assertTrue(output.with_suffix(".png").is_file())

    def test_cli_rejects_bad_render_options_before_starting_engines(self):
        for args in (
            ["--fps", "0"], ["--depth", "0"], ["--duration", "nan"], ["--output", "movie.txt"],
            ["--zanak-seconds", "-1"], ["--zanak-seconds", "nan"], ["--zanak-depth", "129"],
            ["--anak-seconds", "-1"], ["--anak-seconds", "nan"], ["--anak-seconds", "inf"],
            ["--anak-seconds", "0.0001"], ["--zanak-seconds", "0.0001"],
            ["--zanak-seconds", "300", "--timeout", "300"],
            ["--anak-seconds", "300", "--timeout", "300"],
            ["--rebuild", "--replay", "capture.json"],
            ["--rebuild", "--anak", "custom.dll"],
            ["--rebuild", "--zanak", "custom.dll"],
            ["--all-lines", "--depth", "129"],
            ["--all-lines", "--threads", "2"],
            ["--same-speed"],
        ):
            with self.subTest(args=args):
                self.assertEqual(main(args), 1)

    def test_rebuilds_both_local_projects_in_release(self):
        with patch("search_movie.subprocess.run") as run:
            run.return_value.returncode = 0
            rebuild_engines()
        self.assertEqual(run.call_args_list, [
            call(
                ["dotnet", "build", str(ROOT / "src" / project), "-c", "Release", "-t:Rebuild"],
                cwd=str(ROOT),
            )
            for project in ("Anak.Protocol", "Zanak")
        ])

    def test_failed_rebuild_aborts_before_resolving_or_running_engines(self):
        for returncodes in ((1,), (0, 1)):
            with self.subTest(returncodes=returncodes):
                with patch("search_movie.subprocess.run") as run, patch("search_movie.engine_command") as resolve:
                    run.side_effect = [type("Result", (), {"returncode": code})() for code in returncodes]
                    self.assertEqual(main(["--rebuild"]), 1)
                    self.assertEqual(run.call_count, len(returncodes))
                    resolve.assert_not_called()

    def test_cli_sends_shared_or_overridden_zanak_depth_with_time_budget(self):
        async def fake_capture(name, command, board, depth, threads, seconds):
            return Search(name, "e2e4", [report()], depth, seconds)

        with tempfile.TemporaryDirectory() as directory:
            for override, expected_depth, anak_seconds, zanak_seconds in (
                ([], 12, None, 120),
                (["--zanak-depth", "18"], 18, None, 120),
                (["--anak-seconds", "0.05", "--zanak-seconds", "0.1"], 12, 0.05, 0.1),
                (["--anak-seconds", "0", "--zanak-seconds", "0"], 12, None, None),
            ):
                with self.subTest(override=override):
                    with patch("search_movie.engine_command", return_value=["test-engine"]), \
                         patch("search_movie.capture", side_effect=fake_capture) as capture_mock, \
                         patch("search_movie.Movie"), patch("search_movie.render"):
                        self.assertEqual(main([
                            "--depth", "12", "--output", str(Path(directory) / "movie.mp4"), *override,
                        ]), 0)
                        self.assertEqual(capture_mock.call_args_list[0].args[3:], (12, 1, anak_seconds))
                        self.assertEqual(capture_mock.call_args_list[1].args[3:], (expected_depth, 1, zanak_seconds))
                        saved = load_capture(Path(directory) / "movie.json")[2]
                        self.assertEqual([search.limit_seconds for search in saved], [anak_seconds, zanak_seconds])

    def test_encoder_failure_is_not_reported_as_success(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(RuntimeError, "FFmpeg failed"):
                with encoder(Path(directory) / "missing" / "failed.mp4", 2) as stream:
                    stream.write(Image.new("RGB", (1600, 900)).tobytes())

    def test_internal_trace_keeps_non_pv_edges_and_merges_repeated_visits(self):
        root, left, right = ("0000000000000001", "0000000000000002", "0000000000000003")
        lines = [
            {"root": root},
            {"from": root, "to": left, "move": "e2e4"},
            {"from": root, "to": right, "move": "d2d4"},
            {"from": root, "to": left, "move": "e2e4"},
            {"from": right, "to": root, "move": "0000"},
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "trace.jsonl"
            path.write_text("\n".join(json.dumps(line) for line in lines), encoding="utf-8")
            graph = load_trace(path)
            self.assertEqual(graph.edges, [
                TraceEdge(root, left, "e2e4", 2), TraceEdge(root, right, "d2d4"), TraceEdge(right, root, "0000"),
            ])
            self.assertEqual(graph.events, [0, 1, 0, 2])
            searches = [Search(name, "e2e4", [report()], graph=graph) for name in ("Anak", "Zanak")]
            saved = Path(directory) / "capture.json"
            save_capture(saved, chess.Board(), 3, searches)
            self.assertEqual(load_capture(saved)[2], searches)
            self.assertEqual(json.loads(saved.read_text())["view"], "visited-search-graph")
            legacy = json.loads(saved.read_text())
            for item in legacy["searches"]:
                del item["graph"]["events"]
                del item["threads"]
            saved.write_text(json.dumps(legacy))
            legacy_searches = load_capture(saved)[2]
            self.assertIsNone(legacy_searches[0].graph.events)
            self.assertEqual(GraphMovie(chess.Board(), 3, legacy_searches).frame(1).size, (1600, 900))
        for bad in (
            {"source": root, "target": "invalid", "move": "e2e4"},
            {"source": root, "target": left, "move": "e2e9"},
            {"source": root, "target": left, "move": "e2e4", "visits": 0},
            {"source": right, "target": left, "move": "e2e4"},
        ):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                graph_from_records(root, [bad])

    def test_graph_movie_grows_in_visit_order_and_keeps_discovered_positions(self):
        root = "0000000000000000"
        left, right, child = (f"{i:016X}" for i in (1, 2, 3))
        graph = SearchGraph(root, [
            TraceEdge(root, left, "e2e4", 2),
            TraceEdge(left, child, "e7e5"),
            TraceEdge(root, right, "d2d4"),
        ], [0, 1, 0, 2])
        searches = [Search(name, "e2e4", [report()], graph=graph) for name in ("Anak", "Zanak")]
        movie = GraphMovie(chess.Board(), 3, searches)
        with patch.object(movie, "draw_board") as board:
            for progress, nodes, visits in (
                (0, {root}, [0, 0, 0]),
                (0.25, {root, left}, [1, 0, 0]),
                (0.5, {root, left, child}, [1, 1, 0]),
                (0.75, {root, left, child}, [2, 1, 0]),
                (1, {root, left, child, right}, [2, 1, 1]),
            ):
                self.assertEqual(movie.frame(progress).size, (1600, 900))
                self.assertEqual(movie.discovered[0], nodes)
                self.assertEqual(movie.edge_visits[0], visits)
            board.assert_not_called()
        with patch.object(movie, "draw_edge") as edge:
            movie.advance(0, len(graph.events))
            edge.assert_not_called()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "graph.gif"
            render(movie, output, duration=3, fps=2, also_gif=False)
            with Image.open(output) as gif:
                self.assertEqual(gif.n_frames, 6)
            self.assertEqual(movie.edge_visits[0], [2, 1, 1])
        searches[0].graph = None
        with self.assertRaisesRegex(ValueError, "not a PV-only capture"):
            GraphMovie(chess.Board(), 3, searches)

    def test_event_replay_rejects_bad_counts_order_and_indices_but_loads_legacy_graphs(self):
        root, left, right = (f"{i:016X}" for i in (1, 2, 3))
        edges = [TraceEdge(root, left, "e2e4"), TraceEdge(left, right, "e7e5")]
        for events in ([0], [0, 2], [True, 1], [1, 0]):
            with self.subTest(events=events), self.assertRaises(ValueError):
                graph_events(SearchGraph(root, edges, events))
        self.assertEqual(graph_events(SearchGraph(root, edges)), [0, 1])

    def test_timed_trace_round_trip_requires_complete_chronological_timing(self):
        root, left, right = (f"{i:016X}" for i in (1, 2, 3))
        lines = [
            {"root": root},
            {"from": root, "to": left, "move": "e2e4", "seconds": 0.01},
            {"from": root, "to": right, "move": "d2d4", "seconds": 0.04},
            {"from": root, "to": left, "move": "e2e4", "seconds": 0.07},
            {"elapsed_seconds": 0.1},
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "trace.jsonl"
            path.write_text("\n".join(json.dumps(line) for line in lines), encoding="utf-8")
            graph = load_trace(path)
            self.assertEqual(graph.events, [0, 1, 0])
            self.assertEqual(graph.event_seconds, [0.01, 0.04, 0.07])
            self.assertEqual(graph.elapsed_seconds, 0.1)
            searches = [Search(name, "e2e4", [report()], graph=graph) for name in ("Anak", "Zanak")]
            saved = Path(directory) / "capture.json"
            save_capture(saved, chess.Board(), 12, searches)
            self.assertEqual(load_capture(saved)[2], searches)
            for invalid in (
                lines[:-1], lines + [lines[1]],
                [lines[0], {key: value for key, value in lines[1].items() if key != "seconds"}, lines[-1]],
            ):
                with self.subTest(invalid=invalid):
                    path.write_text("\n".join(json.dumps(line) for line in invalid), encoding="utf-8")
                    with self.assertRaises(ValueError):
                        load_trace(path)
        for times, elapsed in (
            ([0.01], 0.1), ([0.04, 0.01, 0.07], 0.1), ([0.01, 0.04, 0.2], 0.1),
            ([0.01, float("nan"), 0.07], 0.1), ([0.01, True, 0.07], 0.1), (None, 0.1),
        ):
            with self.subTest(times=times), self.assertRaises(ValueError):
                validate_graph_timing(SearchGraph(root, graph.edges, graph.events, times, elapsed))

    def test_same_speed_uses_search_time_and_freezes_the_faster_engine(self):
        root, left, right, child = (f"{i:016X}" for i in (1, 2, 3, 4))
        fast = SearchGraph(root, [
            TraceEdge(root, left, "e2e4"), TraceEdge(left, child, "e7e5"),
        ], [0, 1], [0.1, 0.2], 0.5)
        slow = SearchGraph(root, [
            TraceEdge(root, right, "d2d4"), TraceEdge(right, child, "d7d5"),
        ], [0, 1], [0.1, 0.8], 1.0)
        searches = [
            Search("Anak", "e2e4", [report()], graph=fast),
            Search("Zanak", "d2d4", [report()], graph=slow),
        ]
        movie = GraphMovie(chess.Board(), 12, searches, same_speed=True)
        self.assertEqual(movie.state(0, 0), 0)
        self.assertEqual(movie.state(0, 0.3), 2)
        self.assertFalse(movie.finished(0, 0.3))  # Last node is not the search-end time.
        self.assertTrue(movie.finished(0, 0.6))
        self.assertFalse(movie.finished(1, 0.6))
        with patch.object(movie, "text", wraps=movie.text) as labels:
            movie.frame(0.6)
            captions = [call.args[2] for call in labels.call_args_list]
            self.assertEqual(captions.count("DEPTH 02"), 1)
            self.assertIn("TARGET 12", captions)
        self.assertEqual(movie.processed, [2, 1])
        frozen = movie.layers[0].tobytes()
        movie.frame(0.9)
        self.assertEqual(movie.processed, [2, 2])
        self.assertEqual(movie.layers[0].tobytes(), frozen)
        movie.frame(1)
        self.assertTrue(movie.finished(1, 1))
        normal = GraphMovie(chess.Board(), 12, searches)
        self.assertEqual([normal.state(i, 0.6) for i in range(2)], [1, 1])
        searches[0].graph = SearchGraph(root, fast.edges, fast.events)
        with self.assertRaisesRegex(ValueError, "requires timed traces"):
            GraphMovie(chess.Board(), 12, searches, same_speed=True)

    def test_full_trace_capture_requires_engine_support_and_reads_flushed_file(self):
        script = """
import json
import sys
path = None
algorithm = 'Mcts'
threads = 4
for line in sys.stdin:
    if line.strip() == 'uci':
        print('id name Trace Test')
        print('option name SearchTraceFile type string default <empty>')
        print('option name SearchAlgorithm type combo default Mcts var Minimax var Mcts')
        print('option name Threads type spin default 4 min 1 max 128')
        print('uciok', flush=True)
    elif line.strip() == 'isready':
        print('readyok', flush=True)
    elif line.startswith('setoption name SearchTraceFile value '):
        path = line.strip().split(' value ', 1)[1]
    elif line.startswith('setoption name SearchAlgorithm value '):
        algorithm = line.strip().split(' value ', 1)[1]
    elif line.startswith('setoption name Threads value '):
        threads = int(line.strip().split(' value ', 1)[1])
    elif line.startswith('go'):
        if algorithm != 'Minimax':
            raise RuntimeError('Full tracing must select alpha-beta')
        if threads != 1:
            raise RuntimeError('Full tracing must use one thread')
        with open(path, 'w', encoding='utf-8') as stream:
            for record in [
                {'root': '0000000000000001'},
                {'from': '0000000000000001', 'to': '0000000000000002', 'move': 'd2d4', 'seconds': 0.01},
                {'elapsed_seconds': 0.02},
            ]:
                stream.write(json.dumps(record) + '\\n')
        print('info depth 1 score cp 25 nodes 9 time 10 pv e2e4', flush=True)
        print('bestmove e2e4', flush=True)
    elif line.strip() == 'quit':
        break
"""
        result = asyncio.run(asyncio.wait_for(capture(
            "Test", [sys.executable, "-u", "-c", script], chess.Board(), 1, 1, all_lines=True,
        ), 10))
        self.assertEqual(result.graph.edges[0].move, "d2d4")
        self.assertEqual(result.graph.event_seconds, [0.01])
        self.assertEqual(result.graph.elapsed_seconds, 0.02)
        self.assertEqual(result.threads, 1)
        with patch("search_movie.chess.engine.popen_uci") as start:
            with self.assertRaisesRegex(ValueError, "requires --threads 1"):
                asyncio.run(capture("Parallel", ["unused"], chess.Board(), 1, 2, all_lines=True))
            start.assert_not_called()
        unsupported = script.replace("print('option name SearchTraceFile type string default <empty>')", "pass")
        with self.assertRaisesRegex(RuntimeError, "does not support SearchTraceFile"):
            asyncio.run(asyncio.wait_for(capture(
                "Old", [sys.executable, "-u", "-c", unsupported], chess.Board(), 1, 1, all_lines=True,
            ), 10))

    def test_all_lines_cli_uses_shallow_defaults_and_graph_renderer(self):
        async def fake_capture(name, command, board, depth, threads, seconds, all_lines=False):
            self.assertTrue(all_lines)
            return Search(name, "e2e4", [report()], depth, seconds, SearchGraph("0000000000000001", []))

        with tempfile.TemporaryDirectory() as directory:
            with patch("search_movie.engine_command", return_value=["test-engine"]), \
                 patch("search_movie.capture", side_effect=fake_capture) as capture_mock, \
                 patch("search_movie.render") as render_mock:
                self.assertEqual(main(["--all-lines", "--output", str(Path(directory) / "full.mp4")]), 0)
                self.assertEqual(capture_mock.call_args_list[0].args[3:], (3, 1, None))
                self.assertEqual(capture_mock.call_args_list[1].args[3:], (3, 1, 1))
                self.assertIsInstance(render_mock.call_args.args[0], GraphMovie)

    def test_all_lines_allows_high_depth_with_independent_clocks_and_warns(self):
        async def fake_capture(name, command, board, depth, threads, seconds, all_lines=False):
            self.assertTrue(all_lines)
            return Search(name, "e2e4", [report(depth=2)], depth, seconds, SearchGraph("0000000000000001", [], [], [], seconds))

        with tempfile.TemporaryDirectory() as directory:
            with patch("search_movie.engine_command", return_value=["test-engine"]), \
                 patch("search_movie.capture", side_effect=fake_capture) as capture_mock, \
                 patch("search_movie.render") as render_mock, patch("sys.stderr", new_callable=io.StringIO) as errors:
                self.assertEqual(main([
                    "--all-lines", "--same-speed", "--depth", "12", "--zanak-depth", "16",
                    "--anak-seconds", "0.05", "--zanak-seconds", "0.1",
                    "--output", str(Path(directory) / "cutoff.mp4"),
                ]), 0)
                self.assertEqual(capture_mock.call_args_list[0].args[3:], (12, 1, 0.05))
                self.assertEqual(capture_mock.call_args_list[1].args[3:], (16, 1, 0.1))
                self.assertIn("Warning:", errors.getvalue())
                self.assertTrue(render_mock.call_args.args[0].same_speed)
                searches = load_capture(Path(directory) / "cutoff.json")[2]
                self.assertEqual([search.limit_depth for search in searches], [12, 16])
                self.assertEqual([search.reports[-1].depth for search in searches], [2, 2])


if __name__ == "__main__":
    unittest.main()
