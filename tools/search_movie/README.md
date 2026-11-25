# Search Movie

Give Anak and Zanak a position; export a dark, side-by-side **1600 x 900 MP4** for a social post. Includes animated boards, a growing tree, evaluation history, depth, node counts, and the final best moves. GIF export remains optional.

Defaults: **Both engines receive depth 8, Zanak also gets a 120-second time budget, and the MP4 runs for 60 seconds.** Search budgets and movie duration are independent.

**Fidelity:** the default movie shows actual **reported principal variations**, not every visited node. Optional `--all-lines` records both engines with one search thread each, then replays their actual visits as one steadily growing, board-free tree per engine. Neither mode invents branches. Tracing is disabled during normal engine use.

## Setup (Windows PowerShell, from the repository root)

```powershell
python -m pip install -r tools\search_movie\requirements.txt
dotnet build src\Anak.Protocol -c Release
dotnet build src\Zanak -c Release
```

Requires Python 3.10+, the repository's .NET SDK, and the NNUE models in `networks`. FFmpeg comes with `imageio-ffmpeg`; a separate system installation is not needed. Uses Segoe UI/Symbol on Windows, or DejaVu Sans on Linux.

## Make a movie

```powershell
# Starting position: depth 8 for both, up to 2 minutes for Zanak, 60-second MP4
python tools\search_movie\search_movie.py --depth 8

# Force a fresh Release rebuild of both current dev engines before searching
python tools\search_movie\search_movie.py --rebuild --depth 8

# Your position, up to 5 minutes for Zanak, 90-second MP4
python tools\search_movie\search_movie.py `
  --fen "r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/2N2N2/PPPP1PPP/R1BQK2R w KQkq - 4 5" `
  --depth 8 --zanak-seconds 300 --timeout 360 --duration 90 `
  --output artifacts\search_movie\italian.mp4

# Optional short GIF, omitting Zanak's explicit time budget
python tools\search_movie\search_movie.py --depth 6 --zanak-seconds 0 `
  --duration 10 --fps 8 --output artifacts\search_movie\search.gif
```

Default output: `artifacts\search_movie\anak-vs-zanak.mp4` (ignored by Git). Each export also saves:

- A `.json` capture containing **all** scored PV reports, starting FEN, requested depth, and best moves, plus every recorded edge, chronological visit sequence, elapsed timestamps, search-end time and capture thread count in `--all-lines` mode.
- A `.png` final-frame poster.
- With `--also-gif`, a matching looping `.gif`.

The renderer streams frames instead of holding the whole movie in memory. H.264 / YUV420p MP4 is recommended for longer social uploads. GIFs are considerably larger; use `--also-gif` only when wanted, and try `--fps 8 --duration 10` when sharing those. `--duration` accepts 3-600 seconds.

### Independent search budgets

- `--depth 8` now controls **both** engines, including when a Zanak time budget is supplied.
- `--anak-seconds 0.1` gives Anak both depth and a 100 ms search budget via UCI `go depth 8 movetime 100`. By default Anak has no explicit time budget and searches by depth.
- `--zanak-seconds 120` supplies Zanak with both depth and up to two minutes via UCI `go depth 8 movetime 120000`. Zanak's depth support sets its search horizon while the time budget remains independent.
- `--zanak-depth 20` overrides only Zanak's depth, leaving Anak at `--depth`.
- `--anak-seconds 0` or `--zanak-seconds 0` omits that engine's `movetime`, not its depth. Zanak currently applies its internal fallback clock when no clock or movetime is supplied, so retaining an explicit budget is recommended.
- Positive search budgets must be at least `0.001` seconds because UCI `movetime` uses whole milliseconds.
- `--timeout 300` is an emergency watchdog including startup and cleanup, **not extra thinking time**. It must exceed **both** nonzero engine budgets. Increase it when giving either engine more time.

A time budget is an upper limit, not a forced minimum: Zanak can still stop early under its own search rules. The tool does not alter those rules or either engine. Actual reported times and depths remain visible.

To inspect early clock cutoffs at a deeper target:

```powershell
python tools\search_movie\search_movie.py --all-lines `
  --fen "r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/2N2N2/PPPP1PPP/R1BQK2R w KQkq - 4 5" `
  --depth 12 --anak-seconds 0.05 --zanak-seconds 0.1 `
  --duration 60 --fps 30 `
  --output artifacts\search_movie\middlegame-cutoff.mp4
```

Both searches receive their target depth **and** independent clock limits, not a forced depth completion. The partial trees show only work actually performed before each engine returned. The growing-tree view displays each target depth/time limit alongside the engine's reported depth. `--duration` controls playback only and does not give the engines more thinking time.

Clock checks are cooperative and may overshoot a small budget. The last scored UCI report may precede the final clock stop, so its reported depth, time and nodes are **last-info statistics**, not a guaranteed measurement of the complete trace or the exact stopping instant. Engines may also stop for reasons other than their clock; the tool does not label every incomplete-depth result as a proven time cutoff.

### Same-speed playback: see which engine finishes first

Add `--same-speed` to an `--all-lines` capture:

```powershell
python tools\search_movie\search_movie.py --all-lines --same-speed --rebuild `
  --fen "r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/2N2N2/PPPP1PPP/R1BQK2R w KQkq - 4 5" `
  --depth 12 --anak-seconds 0.05 --zanak-seconds 0.1 `
  --duration 60 --fps 30 `
  --output artifacts\search_movie\middlegame-same-speed.mp4
```

- Without the flag, each engine's visit sequence independently fills the movie, so both finish together visually.
- With the flag, both engines use **the same recorded search-time scale**, not equal nodes per second. Their virtual search starts align at zero, and the longer search determines the shared playback range.
- Each visit appears at its recorded elapsed timestamp. An engine's tree freezes and shows **FINISHED** when its recorded search ends, while the other can keep growing. The end timestamp is recorded separately from the last move and the last UCI score report.
- `--duration` stretches this one common clock for readability without changing either search budget. Increasing FPS does not make an engine search faster.
- With a large timing difference, the faster tree may finish in only a few frames. Increase duration/FPS if necessary; independent playback remains available when following every step matters more than comparing completion times.
- Timing starts inside the search trace, excluding process startup, network configuration and video encoding. Both engines are captured sequentially to avoid competing for CPU, then replayed on the shared virtual clock. Tracing itself adds overhead: this is a visual diagnostic, not a clean throughput benchmark.
- Existing captures without per-visit timestamps and a search-end timestamp still replay normally. `--same-speed` rejects them explicitly; recapture with `--all-lines --rebuild`. New timed captures can be replayed with `--same-speed --replay ...`.

Earlier tool versions omitted Zanak's depth whenever `--zanak-seconds` was nonzero. New captures always send a depth; existing JSON captures replay unchanged.

### Every searched line, without boards

```powershell
# Shallow internal search graphs: default depth 3, Zanak budget 1 second
python tools\search_movie\search_movie.py --all-lines --rebuild `
  --fen "7k/8/3p4/2pP4/1pP5/1P6/8/K7 w - - 0 45" `
  --output artifacts\search_movie\locked-endgame-all.mp4

# Smaller search for a busier position
python tools\search_movie\search_movie.py --all-lines --depth 2 `
  --zanak-seconds 0.2 --output artifacts\search_movie\all-lines.mp4

# Faster playback of the same captured exploration (not a new search)
python tools\search_movie\search_movie.py `
  --replay artifacts\search_movie\locked-endgame-all.json `
  --duration 15 --output artifacts\search_movie\locked-endgame-fast.mp4
```

- Shows actual visited search moves, including non-PV branches, not every theoretically possible legal continuation. Unsearched/pruned branches do not appear.
- Anak traces alpha-beta, quiescence, helper-thread searches and null-move probes; full-trace capture explicitly selects `SearchAlgorithm=Minimax` rather than MCTS. Zanak traces root seeds, re-deepening, agent sibling evaluations and their continuation spines. Auxiliary move-ordering/safety checks, the Micro watchdog and replay-only moves are not search-graph edges.
- Uses the engines' opt-in `SearchTraceFile` UCI option. Old/custom binaries without this option fail explicitly; use `--rebuild` for current local binaries.
- `--all-lines` requires `--threads 1` (the default), rather than quietly allowing parallel exploration. For traced one-thread Zanak searches, agents run sequentially on the main search caller, not concurrently on a background worker while the caller also explores. Normal, untraced search scheduling is unchanged.
- Normal PV mode still defaults to depth 8 and 120 seconds for Zanak. `--all-lines` defaults to depth 3 and 1 second for Zanak, but accepts explicit depths up to 128. Depths above 4 emit a size/readability warning rather than failing; use the per-engine clocks to bound search work. Quiescence and search extensions can visit positions beyond the requested depth.
- The movie starts with just the root. Actual trace events add positions and edges cumulatively, including non-PV exploration. Discovered positions never disappear, there are no pages or resets, and unseen branches are not drawn in advance. Stable coordinates prevent existing nodes from jumping around.
- Transpositions and repeated positions are merged by position hash, but repeated visits remain in chronological order: the visited branch lights up again, and `xN` counts visits **so far**, not its final visit count. `null` identifies Anak's hypothetical null-move pruning probes, not a legal game move.
- Older edges stay visible; the current branch and move are highlighted and labelled to keep a busy tree followable. Position, edge and visit counters advance with events. No 12-report/6-ply PV sampling is applied.
- Capturing and playback are separate: search runs first, then its recorded visit order drives persistent drawing layers. Only newly visited edges/positions update those layers; video frames capture the accumulated result rather than redraw/check the entire search graph. A frame can consume multiple events without dropping any. Increase `--duration` to slow the growth, or decrease it to print positions faster; `--fps` controls video smoothness, not search threads or work. By default, visits are independently stretched; `--same-speed` instead aligns both trees on recorded search time.
- There are no chessboards. Final evaluation, node counts and best moves remain visible. Search tracing adds diagnostic overhead, so these captures are **not throughput benchmarks**.
- New graph captures automatically replay in growing-tree mode. Older graph captures retain first-discovery order but cannot recover the chronology of repeated visits or verify that capture used one worker; replay warns about that limitation. Recapture for complete visit playback. An old PV-only capture cannot be converted into an internal trace with `--all-lines`; capture a new search instead.

### Render again without searching

```powershell
python tools\search_movie\search_movie.py `
  --replay artifacts\search_movie\italian.json `
  --output artifacts\search_movie\italian-short.mp4 --duration 10
```

Use a different output stem to preserve the original capture. Replaying only changes the movie: it does not give a previously captured search more time. `--anak` and `--zanak` accept explicit executable or DLL paths; otherwise the most recently built local Release DLL is selected.

`--rebuild` runs `dotnet build -c Release -t:Rebuild` for both local engine projects before selecting the binaries. It uses your current working-tree sources, including uncommitted changes, without any Git operations. Build output is shown, and a failed build aborts capture instead of using stale binaries. It cannot be combined with `--replay`, `--anak`, or `--zanak`. Without the flag, existing builds are reused as before.

## Reading the animation

- Mint = Anak; amber = Zanak. Bright edges show the current reported line; dim edges preserve earlier reported lines.
- Each line is revealed **move by move**, updating the board from the input position. The board resets to that position for the next report.
- Up to **12 evenly sampled reports and 6 plies** are displayed to keep the tree readable. The JSON retains complete lines and all reports.
- Scores and their bounds are converted to **White's perspective**; `M+3` means White mates in three. The small chart shows evaluation changes across reports, not time.
- Both searches are independently stretched to fit the movie. **This is not a speed race or benchmark.** Displayed node counts and engine times are the real UCI values.
- The heading shows each engine's actual configured limit (depth and/or time); these limits are also saved in the JSON. Old captures without budget metadata still replay using their original shared-depth label.
- Neither the requested limits nor reported depths promise equal work or equivalent depth semantics between alpha-beta and ZAP. Searches can stop early.
- Opening books and Syzygy shortcuts are disabled for the capture so positions are actually searched. NNUE remains enabled; search threads default to one.
- Captures come from fresh engine processes. Zanak worker scheduling can still make repeated captures differ.

## Tests

```powershell
python -m unittest discover -s tools\search_movie -p "test_*.py" -v
dotnet test -c Release
```
