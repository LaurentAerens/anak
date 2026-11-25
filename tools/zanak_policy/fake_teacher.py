"""Minimal UCI fixture used only by the focused unit tests."""
import os
import sys
import time


def emit(line):
    print(line, flush=True)


for raw in sys.stdin:
    command = raw.strip()
    with open(sys.argv[1], "a", encoding="utf-8") as log:
        log.write(command + "\n")
    if command == "uci":
        emit("id name fake")
        emit("option name Threads type spin default 2 min 1 max 8")
        emit("option name SearchAlgorithm type combo default Mcts var Minimax var Mcts")
        emit("option name OwnBook type check default true")
        emit("option name SyzygyPath type string default <empty>")
        emit("uciok")
    elif command == "isready":
        emit("readyok")
    elif command.startswith("go "):
        mode = os.environ.get("FAKE_TEACHER_MODE")
        if mode == "hang":
            time.sleep(10)
        elif mode == "exit":
            sys.exit(2)
        elif mode == "incomplete":
            emit("info depth 8 score cp 23 pv e2e4")
            emit("bestmove e2e4")
        elif mode == "search_error":
            emit("info string search error: invalid position")
            emit("bestmove 0000")
        else:
            emit("info depth 8 score cp 23 nodes 100 pv e2e4 e7e5")
            emit("bestmove e2e4 ponder e7e5")
    elif command == "quit":
        break
