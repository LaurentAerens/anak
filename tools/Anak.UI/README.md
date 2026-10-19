# Anak Chess Engine — Local Debugging UI

A lightweight, interactive GUI tool designed specifically for local development, visual debugging, engine tuning, and testing of the **Anak** chess engine.

> [!NOTE]
> This tool is strictly a developer utility residing in `tools/` and is **not** packaged into the shipped standalone engine executable (`anak.exe`).

---

## Features

- **Interactive Board**: Click-to-move square interaction with visual indicators for legal moves, captures, check state, and previous move highlights.
- **Board Flipping**: Supports playing or viewing from both White's and Black's perspectives.
- **Live MCTS Diagnostics**: Real-time telemetry during engine search including depth, selective depth, nodes searched, nodes per second (NPS), centipawn/mate score, and principal variation (PV).
- **Evaluation Bar**: Dynamic vertical bar visualizing White vs. Black win probability derived from MCTS action-values ($Q$).
- **Engine Control**: Trigger engine search on demand, cancel search, or enable automated self-play mode.
- **Move History**: Complete move log in standard algebraic notation.
- **FEN Inspection**: Direct FEN loading and copying for rapid setup of tactical puzzles and endgame positions.

---

## How to Run

To launch the visualizer on Windows:

```bash
dotnet run --project tools/Anak.UI -c Release
```

---

## Architecture & Cross-Platform Support

`Anak.UI` directly references `Anak.Core`, `Anak.Search`, `Anak.Data`, and `Anak.Protocol`.

To maintain cross-platform CI compatibility:
- On **Windows**, it compiles against `net11.0-windows` using Windows Forms and GDI+ double-buffered rendering.
- On **Linux / macOS**, it falls back gracefully to a headless console message, allowing cross-platform CI pipelines (`dotnet build`, `dotnet test`) to build the entire solution without platform-specific SDK errors.

---

## Open-Source Attribution

This debugging visualizer draws design inspiration and architectural patterns from open-source C# chess projects:
- **Sebastian Lague** — [*Chess-Coding-Adventure*](https://github.com/SebLague/Chess-Coding-Adventure) (MIT License)
- WinForms Chess UI community visualizer patterns (MIT License)

Licensed under the **GNU General Public License v3.0 (GPLv3)** in alignment with the Anak engine project.
