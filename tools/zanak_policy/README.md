# Offline Anak teacher annotations

This standalone Python 3.9+ tool prepares labels for **offline** Zanak policy
calibration. It does not change Zanak's runtime policy or add search to its
runtime path. The workflow is ready to annotate 100,000+ lines; running the
tool is still required to produce a corpus.

## Run (Windows PowerShell, from the repository root)

```powershell
python tools\zanak_policy\annotate.py --input positions.epd --output teacher.jsonl --engine C:\path\to\Anak.Protocol.dll --nodes 50000 --timeout 120
```

Use the actual built Anak UCI executable instead of the DLL if preferred. DLLs
require `dotnet` on `PATH`. Use `--depth 12` instead of `--nodes` for a depth
budget. The positive, finite timeout applies separately to initialization,
each readiness check, and each search. If a teacher stalls or exits, the
tool records an error, terminates it, and stops; rerun the same command to
retry that position. The process is persistent across successful positions.

Input is UTF-8, one FEN or EPD per line. Four-field EPDs receive `0 1` move
counters; EPD operations are ignored. Six-field FENs retain their counters.
Blank and structurally invalid lines become error records, not silent skips.
Validation is structural, not a complete chess-legality check: curate the
input, and inspect teacher errors. Searches use a new UCI game per position.

The tool requests `Threads=1`, `SearchAlgorithm=Minimax`, `OwnBook=false`,
and an empty `SyzygyPath` when those options are advertised. Missing options
produce stderr warnings: a teacher that does not advertise them may still use
shortcuts or a different algorithm. Verify the selected teacher's behavior.
An advertised SearchAlgorithm with no Minimax choice is an error.

## Output and resuming

Each JSONL row includes the 1-based input `line`, original `source_fen`
(including any EPD operations), normalized `fen` where available, teacher
command, budget, configuration fingerprint, and `status`:

* `ok`: `bestmove`, `score` (`cp` or `mate`, optionally a UCI bound), `pv`,
  `depth`, and `nodes`. Scores are from the side-to-move perspective as
  reported by UCI. Terminal `0000`/`(none)` moves can have null statistics.
* `input_error`: descriptive `error`; retained on resume.
* `teacher_error`: descriptive `error`; retried on resume. A nonterminal
  result without a complete, matching PV, score, depth and node count is an
  error, not a label.

Exit status is 0 for no input or teacher errors, 1 when an input or teacher
error exists, and 2 for a configuration or file error. Errors are written to
JSONL and reported on stderr. On resume, previously successful and invalid
input lines are skipped, including duplicates at different line numbers.
Keep the input lines and their order unchanged; appending lines is allowed.
Existing rows are checked against the input and command/budget fingerprint.
The fingerprint identifies the command path and budget, **not the binary
contents**: do not replace the executable or DLL while resuming. Use a new
output file for a changed teacher or budget.

Rows are flushed individually. A malformed or incomplete JSONL tail is
reported rather than silently discarded: back up the file and repair the
tail explicitly before resuming. Use a local disk with adequate space and
avoid running multiple writers against the same output. Completed annotation
records are not a guarantee of legal input or label quality.

## Focused protocol tests

```powershell
python -m unittest discover -s tools\zanak_policy -p test_annotate.py -v
```

Tests use a fake UCI teacher and the standard library only; they do not
invoke Anak or validate playing strength.
