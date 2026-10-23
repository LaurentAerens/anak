# Anak Chess Engine — SPSA Tuning Framework

Production-ready, pure-Python **Simultaneous Perturbation Stochastic Approximation (SPSA)** tuning harness for the Anak chess engine.

SPSA is the standard gradient-free optimization algorithm used by top chess engines (Stockfish, Berserk, Ethereal) to tune search heuristics, pruning margins, and evaluation parameters against stochastic game noise.

---

## 1. Targeted Search Heuristics

The tuner optimizes 5 critical parameters in Anak's alpha-beta minimax search:

| Parameter | UCI Option | Default | Range | Description |
| :--- | :--- | :---: | :---: | :--- |
| **LMR Base** | `LmrBase` | `75` (0.75) | `20 .. 150` | Constant term in Late Move Reduction formula: $R = \text{base} + \frac{\ln(d) \cdot \ln(m)}{\text{div}}$ |
| **LMR Division** | `LmrDivision` | `225` (2.25) | `100 .. 400` | Logarithmic divisor scaling LMR reductions for deep/late quiet moves |
| **RFP Margin** | `RfpMargin` | `75` | `30 .. 150` | Reverse Futility Pruning (Static Null Move) multiplier: $\text{margin} = \text{rfp} \cdot \text{depth}$ |
| **NMP Base** | `NmpBase` | `3` | `1 .. 6` | Base reduction for dynamic Null Move Pruning: $R = \text{nmp} + \lfloor d / 6 \rfloor + \dots$ |
| **Aspiration Delta** | `AspirationDelta` | `25` | `10 .. 60` | Initial search aspiration window radius: $[\text{score} - \delta, \text{score} + \delta]$ |

---

## 2. Quick Dry-Run Verification

To verify parameter perturbation, gradient calculation, JSON tracking, and C# snippet generation without requiring `fastchess` or a compiled engine:

```bash
python tools/tuning/spsa.py --dry-run --steps 10 --games-per-step 20
```

---

## 3. Launching Live SPSA Tuning on Multi-Core Systems

### Step 1: Ensure Anak Engine is Published

Publish Anak in `Release` configuration:

```bash
dotnet publish src/Anak.Protocol/Anak.Protocol.csproj -c Release -o tools/fastchess/bin/anak
```

### Step 2: Launch the Tuner

#### On an 8-Core System:
```bash
python tools/tuning/spsa.py --steps 50 --games-per-step 30 --concurrency 8 --tc 2+0.02
```

#### On a 16-Core / 32-Thread System:
```bash
python tools/tuning/spsa.py --steps 100 --games-per-step 40 --concurrency 16 --tc 2+0.02
```

#### Ultra-Fast Smoke Tuning (1+0.01 Time Control):
```bash
python tools/tuning/spsa.py --steps 25 --games-per-step 20 --concurrency 12 --tc 1+0.01
```

---

## 4. Resuming an In-Progress Session

If a tuning run is interrupted (or you wish to continue training), simply pass `--resume`:

```bash
python tools/tuning/spsa.py --resume --steps 100 --concurrency 16
```

The harness automatically restores parameter states, step history, and decay counters from `tools/tuning/spsa_progress.json`.

---

## 5. Command-Line Options

| Flag | Default | Description |
| :--- | :---: | :--- |
| `--steps` | `50` | Total SPSA optimization steps / matches |
| `--games-per-step` | `30` | Number of games per step match (15 opening pairs with swapped colors) |
| `--tc` | `2+0.02` | Time control (seconds base + increment) |
| `--concurrency` | `min(16, CPU)` | Parallel games executed by `fastchess` |
| `--hash` | `16` | Hash table size in MB per engine instance |
| `--dry-run` | `False` | Run simulated matches without invoking `fastchess` |
| `--resume` | `False` | Resume previous tuning session from `spsa_progress.json` |
| `--fastchess` | Auto | Custom path to `fastchess` executable |
| `--anak` | Auto | Custom path to `anak` executable |
| `--openings` | Auto | Custom path to openings suite (`.epd`) |
| `--output` | `spsa_progress.json` | Path to progress tracking JSON file |

---

## 6. Applying Tuned Parameters to Anak

At the end of each step and upon completion, `spsa.py` prints ready-to-copy C# code snippets and saves them into `tools/tuning/spsa_progress.json`:

```csharp
// In src/Anak.Search/Minimax/Pruning/SearchHeuristics.cs:
public const int TunedLmrBase = 80;             // default: 75  (0.80)
public const int TunedLmrDivision = 235;        // default: 225 (2.35)
public const int TunedRfpMargin = 72;           // default: 75
public const int TunedNmpBase = 3;              // default: 3
public const int TunedAspirationDelta = 28;      // default: 25

// In SearchHeuristics.cs static constructor:
double reduction = 0.80 + (Math.Log(depth) * Math.Log(moves) / 2.35);

// In SearchHeuristics.GetNmpReduction:
int baseR = 3 + (depth / 6);

// In AlphaBetaEngine.cs (Reverse Futility Pruning):
int rfpMargin = 72 * depth;

// In AlphaBetaEngine.cs (Aspiration Windows):
int delta = 28;
```
