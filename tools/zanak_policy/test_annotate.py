import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import annotate


START = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq -"


class ParsingTests(unittest.TestCase):
    def test_epd_and_fen(self):
        self.assertEqual(annotate.parse_position(START + ' id "start";'), START + " 0 1")
        self.assertEqual(annotate.parse_position(START + " 4 9"), START + " 4 9")
        for bad in ("garbage", START.replace(" w ", " x "), START + " 0 nope",
                    START.replace("8/8/8", "9/8/8"), START + "\nquit"):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                annotate.parse_position(bad)

    def test_info_score_and_pv(self):
        result = annotate.parse_info(
            "info depth 12 seldepth 18 score mate -3 nodes 1234 nps 100 pv e2e4 e7e5"
        )
        self.assertEqual(result["score"], {"type": "mate", "value": -3})
        self.assertEqual(result["pv"], ["e2e4", "e7e5"])
        self.assertEqual((result["depth"], result["nodes"]), (12, 1234))


class AnnotationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "positions.epd"
        self.output = self.root / "out.jsonl"
        self.log = self.root / "commands.txt"
        self.fake = Path(__file__).with_name("fake_teacher.py")
        self.command = [sys.executable, str(self.fake), str(self.log)]

    def run_annotation(self, timeout=1):
        return annotate.annotate_file(
            self.source, self.output, self.command, "nodes", 100, timeout
        )

    def records(self):
        return [json.loads(line) for line in self.output.read_text(encoding="utf-8").splitlines()]

    def test_persistent_teacher_options_results_errors_and_resume(self):
        self.source.write_text(START + '\ninvalid fen\n' + START + ' id "again";\n',
                               encoding="utf-8")
        self.assertEqual(self.run_annotation(), 1)
        rows = self.records()
        self.assertEqual(len(rows), 3)
        self.assertEqual(rows[0]["bestmove"], "e2e4")
        self.assertEqual(rows[0]["score"], {"type": "cp", "value": 23})
        self.assertEqual(rows[0]["pv"], ["e2e4", "e7e5"])
        self.assertEqual((rows[0]["depth"], rows[0]["nodes"]), (8, 100))
        self.assertEqual(rows[0]["budget"], {"kind": "nodes", "value": 100})
        self.assertEqual(rows[0]["teacher"], self.command)
        self.assertIn("error", rows[1])
        self.assertEqual(rows[2]["source_fen"], START + ' id "again";')
        commands = self.log.read_text(encoding="utf-8").splitlines()
        self.assertEqual(commands.count("uci"), 1)
        self.assertEqual(commands.count("go nodes 100"), 2)
        for option in ("setoption name Threads value 1",
                       "setoption name SearchAlgorithm value Minimax",
                       "setoption name OwnBook value false",
                       "setoption name SyzygyPath value"):
            self.assertIn(option, commands)
        self.assertEqual(self.run_annotation(), 1)
        self.assertEqual(len(self.records()), 3)
        self.assertEqual(commands, self.log.read_text(encoding="utf-8").splitlines())

    def test_changed_input_or_budget_is_rejected(self):
        self.source.write_text(START + "\n", encoding="utf-8")
        self.assertEqual(self.run_annotation(), 0)
        self.source.write_text(START + " 1 1\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "input"):
            self.run_annotation()
        self.source.write_text(START + "\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "configuration"):
            annotate.annotate_file(self.source, self.output, self.command, "depth", 2, 1)

    def test_timeout_records_error_and_retries_on_resume(self):
        self.source.write_text(START + "\n", encoding="utf-8")
        os.environ["FAKE_TEACHER_MODE"] = "hang"
        self.addCleanup(os.environ.pop, "FAKE_TEACHER_MODE", None)
        self.assertEqual(self.run_annotation(timeout=0.15), 1)
        self.assertIn("timed out", self.records()[0]["error"])
        os.environ.pop("FAKE_TEACHER_MODE")
        self.assertEqual(self.run_annotation(), 0)
        self.assertEqual(len(self.records()), 2)
        self.assertEqual(self.records()[1]["bestmove"], "e2e4")

    def test_timeout_cleanup_reaps_process(self):
        os.environ["FAKE_TEACHER_MODE"] = "hang"
        self.addCleanup(os.environ.pop, "FAKE_TEACHER_MODE", None)
        teacher = annotate.Teacher(self.command, 0.3)
        try:
            with self.assertRaisesRegex(annotate.TeacherError, "timed out"):
                teacher.search(START + " 0 1", "depth", 2)
        finally:
            teacher.close()
        self.assertIsNotNone(teacher.process.poll())

    def test_cli_writes_invalid_input_without_launching_teacher(self):
        self.source.write_text("invalid\n", encoding="utf-8")
        result = subprocess.run(
            [sys.executable, str(Path(annotate.__file__)), "--input", str(self.source),
             "--output", str(self.output), "--engine", str(self.root / "missing.exe"),
             "--depth", "2"], capture_output=True, text=True, timeout=5,
        )
        self.assertEqual(result.returncode, 1)
        self.assertIn("line 1:", result.stderr)
        self.assertEqual(self.records()[0]["status"], "input_error")

    def test_corrupt_output_is_not_silently_discarded(self):
        self.source.write_text(START + "\n", encoding="utf-8")
        self.output.write_text('{"incomplete":', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "JSONL"):
            self.run_annotation()

    def test_missing_final_newline_is_rejected(self):
        self.source.write_text(START + "\n", encoding="utf-8")
        self.assertEqual(self.run_annotation(), 0)
        self.output.write_text(self.output.read_text(encoding="utf-8").rstrip("\n"), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "newline"):
            self.run_annotation()

    def test_incomplete_teacher_info_is_error(self):
        self.source.write_text(START + "\n", encoding="utf-8")
        os.environ["FAKE_TEACHER_MODE"] = "incomplete"
        self.addCleanup(os.environ.pop, "FAKE_TEACHER_MODE", None)
        self.assertEqual(self.run_annotation(), 1)
        self.assertIn("complete score", self.records()[0]["error"])

    def test_engine_search_error_is_not_a_terminal_position(self):
        self.source.write_text(START + "\n", encoding="utf-8")
        os.environ["FAKE_TEACHER_MODE"] = "search_error"
        self.addCleanup(os.environ.pop, "FAKE_TEACHER_MODE", None)
        self.assertEqual(self.run_annotation(), 1)
        self.assertIn("search error: invalid position", self.records()[0]["error"])

    def test_teacher_exit_is_reported(self):
        self.source.write_text(START + "\n", encoding="utf-8")
        os.environ["FAKE_TEACHER_MODE"] = "exit"
        self.addCleanup(os.environ.pop, "FAKE_TEACHER_MODE", None)
        self.assertEqual(self.run_annotation(), 1)
        self.assertIn("exited", self.records()[0]["error"])


if __name__ == "__main__":
    unittest.main()
