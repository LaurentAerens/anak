"""Offline, resumable UCI teacher annotations for Zanak policy calibration."""
import argparse
from collections import deque
import hashlib
import json
import math
from pathlib import Path
import queue
import re
import subprocess
import sys
import threading
import time


MOVE = re.compile(r"^[a-h][1-8][a-h][1-8][qrbn]?$")
OPTION = re.compile(r"^option name (.+?) type (\S+)(?: (.*))?$", re.IGNORECASE)


def parse_position(source):
    """Extract a structurally valid six-field FEN from one FEN/EPD line."""
    if any(ord(char) < 32 and char != "\t" for char in source):
        raise ValueError("control character in input")
    fields = source.split()
    if len(fields) < 4:
        raise ValueError("expected at least four FEN/EPD fields")
    board, side, castling, ep = fields[:4]
    ranks = board.split("/")
    if len(ranks) != 8:
        raise ValueError("FEN board must have eight ranks")
    for rank in ranks:
        if not rank or any(c not in "12345678prnbqkPRNBQK" for c in rank):
            raise ValueError("invalid FEN rank")
        if sum(int(c) if c.isdigit() else 1 for c in rank) != 8:
            raise ValueError("FEN rank must have eight squares")
    if side not in ("w", "b"):
        raise ValueError("invalid side to move")
    if castling != "-" and (not re.fullmatch(r"K?Q?k?q?", castling) or not castling):
        raise ValueError("invalid castling rights")
    if ep != "-" and not re.fullmatch(r"[a-h][36]", ep):
        raise ValueError("invalid en passant square")
    if len(fields) > 4 and fields[4].isdigit():
        if len(fields) < 6 or not fields[5].isdigit() or int(fields[5]) < 1:
            raise ValueError("invalid FEN move counters")
        counters = fields[4:6]
    else:
        counters = ["0", "1"]
        if len(fields) > 4 and not re.match(r"^[A-Za-z_]", fields[4]):
            raise ValueError("invalid EPD operation or FEN counter")
    return " ".join(fields[:4] + counters)


def parse_info(line):
    tokens = line.split()
    result = {}
    for key in ("depth", "nodes", "multipv"):
        if key in tokens:
            index = tokens.index(key)
            if index + 1 < len(tokens) and tokens[index + 1].isdigit():
                result[key] = int(tokens[index + 1])
    if "score" in tokens:
        index = tokens.index("score")
        if index + 2 < len(tokens) and tokens[index + 1] in ("cp", "mate"):
            try:
                score = {"type": tokens[index + 1], "value": int(tokens[index + 2])}
                if index + 3 < len(tokens) and tokens[index + 3] in ("lowerbound", "upperbound"):
                    score["bound"] = tokens[index + 3]
                result["score"] = score
            except ValueError:
                pass
    if "pv" in tokens:
        result["pv"] = tokens[tokens.index("pv") + 1:]
    return result


class TeacherError(Exception):
    pass


class Teacher:
    def __init__(self, command, timeout):
        self.timeout = timeout
        self.lines = queue.Queue()
        self.stderr = deque(maxlen=20)
        try:
            self.process = subprocess.Popen(
                command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace",
                bufsize=1,
            )
        except OSError as exc:
            raise TeacherError("cannot start teacher: " + str(exc)) from exc
        threading.Thread(target=self._read_stdout, daemon=True).start()
        threading.Thread(target=self._read_stderr, daemon=True).start()
        try:
            self.send("uci")
            options = {}
            for line in self.until("uciok"):
                match = OPTION.match(line)
                if match:
                    options[match.group(1).lower()] = (match.group(1), match.group(3) or "")
            settings = (("threads", "1"), ("searchalgorithm", "Minimax"),
                        ("ownbook", "false"), ("syzygypath", ""))
            for key, value in settings:
                if key not in options:
                    print("warning: teacher does not advertise " + key, file=sys.stderr)
                    continue
                name, declaration = options[key]
                if key == "searchalgorithm":
                    variants = re.findall(r"(?:^| )var ([^ ]+)", declaration, re.IGNORECASE)
                    if variants and not any(v.lower() == "minimax" for v in variants):
                        raise TeacherError("teacher does not offer Minimax")
                self.send("setoption name " + name + " value" + (" " + value if value else ""))
            self.send("isready")
            self.until("readyok")
        except BaseException:
            self.close()
            raise

    def _read_stdout(self):
        try:
            for line in self.process.stdout:
                self.lines.put(line.rstrip("\r\n"))
        finally:
            self.lines.put(None)

    def _read_stderr(self):
        for line in self.process.stderr:
            self.stderr.append(line.rstrip())

    def send(self, command):
        try:
            self.process.stdin.write(command + "\n")
            self.process.stdin.flush()
        except (BrokenPipeError, OSError, ValueError) as exc:
            raise TeacherError("teacher exited or closed stdin: " + str(exc)) from exc

    def until(self, marker):
        deadline = time.monotonic() + self.timeout
        collected = []
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TeacherError("teacher timed out waiting for " + marker)
            try:
                line = self.lines.get(timeout=remaining)
            except queue.Empty as exc:
                raise TeacherError("teacher timed out waiting for " + marker) from exc
            if line is None:
                detail = "; ".join(self.stderr)
                raise TeacherError("teacher exited waiting for " + marker +
                                   (": " + detail if detail else ""))
            if line == marker or line.startswith(marker + " "):
                return collected + [line]
            collected.append(line)

    def search(self, fen, budget_kind, budget):
        self.send("ucinewgame")
        self.send("isready")
        self.until("readyok")
        self.send("position fen " + fen)
        self.send("go " + budget_kind + " " + str(budget))
        lines = self.until("bestmove")
        parts = lines[-1].split()
        if len(parts) < 2 or not (MOVE.fullmatch(parts[1]) or parts[1] in ("0000", "(none)")):
            raise TeacherError("invalid teacher bestmove: " + lines[-1])
        info = {}
        for line in lines[:-1]:
            if line.lower().startswith("info string search error"):
                raise TeacherError(line.removeprefix("info string "))
            if line.startswith("info "):
                candidate = parse_info(line)
                if candidate.get("multipv", 1) == 1 and all(
                        key in candidate for key in ("score", "pv", "depth", "nodes")):
                    info = candidate
        bestmove = parts[1]
        if bestmove not in ("0000", "(none)") and (
                not info or not info["pv"] or info["pv"][0] != bestmove or
                not all(MOVE.fullmatch(move) for move in info["pv"])):
            raise TeacherError("teacher did not provide a complete score, depth, nodes and matching PV")
        return {"bestmove": bestmove, "score": info.get("score"),
                "pv": info.get("pv", []), "depth": info.get("depth"),
                "nodes": info.get("nodes")}

    def close(self):
        if self.process.poll() is None:
            try:
                self.send("quit")
                self.process.wait(timeout=min(self.timeout, 1))
            except (TeacherError, subprocess.TimeoutExpired):
                self.process.kill()
                self.process.wait()
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
            if stream:
                try:
                    stream.close()
                except OSError:
                    pass


def configuration_id(command, budget_kind, budget):
    config = json.dumps([command, budget_kind, budget], ensure_ascii=True, separators=(",", ":"))
    return hashlib.sha256(config.encode("utf-8")).hexdigest()


def load_resume(output, config):
    records = {}
    if not output.exists():
        return records
    with output.open(encoding="utf-8") as file:
        for number, line in enumerate(file, 1):
            if not line.endswith("\n"):
                raise ValueError(f"JSONL record at line {number} is missing its final newline")
            try:
                row = json.loads(line)
                if not isinstance(row, dict) or not isinstance(row.get("line"), int) or \
                        row["line"] < 1 or not isinstance(row.get("source_fen"), str) or \
                        row.get("status") not in ("ok", "input_error", "teacher_error"):
                    raise ValueError("invalid record")
            except (ValueError, json.JSONDecodeError) as exc:
                raise ValueError(f"invalid JSONL record at line {number}: {exc}") from exc
            if row.get("configuration") != config:
                raise ValueError("output configuration differs from this run")
            previous = records.get(row["line"])
            if previous and previous["source_fen"] != row["source_fen"]:
                raise ValueError("inconsistent input in JSONL")
            records[row["line"]] = row
    return records


def annotate_file(source, output, command, budget_kind, budget, timeout):
    source, output = Path(source), Path(output)
    if source.resolve() == output.resolve():
        raise ValueError("input and output must be different files")
    if budget_kind not in ("nodes", "depth") or budget <= 0:
        raise ValueError("budget must be a positive node or depth count")
    if not math.isfinite(timeout) or timeout <= 0:
        raise ValueError("timeout must be positive and finite")
    config = configuration_id(command, budget_kind, budget)
    records = load_resume(output, config)
    remaining = set(records)
    with source.open(encoding="utf-8-sig") as file:
        for number, line in enumerate(file, 1):
            if number in records:
                if records[number]["source_fen"] != line.rstrip("\r\n"):
                    raise ValueError(f"input changed at line {number}")
                remaining.discard(number)
    if remaining:
        raise ValueError("input is shorter than the existing output")

    teacher = None
    errors = 0
    try:
        with source.open(encoding="utf-8-sig") as file, output.open("a", encoding="utf-8", newline="\n") as out:
            for number, line in enumerate(file, 1):
                text = line.rstrip("\r\n")
                previous = records.get(number)
                if previous and previous["status"] in ("ok", "input_error"):
                    errors += previous["status"] == "input_error"
                    continue
                row = {"line": number, "source_fen": text, "configuration": config,
                       "teacher": command, "budget": {"kind": budget_kind, "value": budget}}
                try:
                    fen = parse_position(text)
                except ValueError as exc:
                    row.update(status="input_error", error=str(exc))
                    errors += 1
                else:
                    row["fen"] = fen
                    try:
                        if teacher is None:
                            teacher = Teacher(command, timeout)
                        row.update(teacher.search(fen, budget_kind, budget), status="ok")
                    except TeacherError as exc:
                        row.update(status="teacher_error", error=str(exc))
                        errors += 1
                out.write(json.dumps(row, ensure_ascii=False) + "\n")
                out.flush()
                if row["status"] != "ok":
                    print(f"line {number}: {row['error']}", file=sys.stderr)
                if row["status"] == "teacher_error":
                    break
    finally:
        if teacher is not None:
            teacher.close()
    return 1 if errors else 0


def positive_int(value):
    try:
        number = int(value)
        if number > 0:
            return number
    except ValueError:
        pass
    raise argparse.ArgumentTypeError("must be a positive integer")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True, help="FEN/EPD input, one position per line")
    parser.add_argument("--output", type=Path, required=True, help="resumable JSONL output")
    parser.add_argument("--engine", type=Path, required=True, help="Anak UCI executable or DLL")
    budget = parser.add_mutually_exclusive_group(required=True)
    budget.add_argument("--nodes", type=positive_int)
    budget.add_argument("--depth", type=positive_int)
    parser.add_argument("--timeout", type=float, default=120.0, help="seconds per UCI phase (default: 120)")
    args = parser.parse_args(argv)
    command = [str(args.engine.resolve())]
    if args.engine.suffix.lower() == ".dll":
        command.insert(0, "dotnet")
    try:
        return annotate_file(args.input, args.output, command,
                             "nodes" if args.nodes else "depth",
                             args.nodes or args.depth, args.timeout)
    except (ValueError, OSError) as exc:
        print("error: " + str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
