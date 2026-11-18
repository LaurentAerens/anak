using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Anak.Core;
using Anak.Data.Evaluation;
using Anak.Data.Syzygy;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;

namespace Anak.Data.Nnue;

/// <summary>
/// Multi-Expert NNUE evaluator supporting both:
/// 1. Unified Shared-FT Multi-Head MoE (<c>ArchMoE 0x0003</c>, <c>anak.nnue</c>) with zero-cost leaf-level
///    expert routing across all 6 heads and smooth Open/Closed boundary interpolation.
/// 2. Legacy 6-network expert routing (<c>open.nnue</c>, <c>closed.nnue</c>, <c>endgame.nnue</c>,
///    <c>queenless.nnue</c>, <c>imbalance.nnue</c>, <c>attack.nnue</c>), with fallback to DefaultEvaluator.
/// </summary>
[SkipLocalsInit]
public sealed unsafe class MultiTierNnueEvaluator : IMultiTierEvaluator, IIncrementalEvaluator, IDisposable
{
    private NnueWeights? _moeWeights;
    private NnueWeights? _openWeights;
    private NnueWeights? _closedWeights;
    private NnueWeights? _endgameWeights;
    private NnueWeights? _queenlessWeights;
    private NnueWeights? _imbalanceWeights;
    private NnueWeights? _attackWeights;
    private NnueWeights? _activeFullWeights;
    private readonly DefaultEvaluator _fallbackEvaluator;
    private readonly short[] _stackWhite = new short[256 * 512];
    private readonly short[] _stackBlack = new short[256 * 512];
    private readonly ulong[] _stackKeys = new ulong[256];
    private int _stackDepth = -1;
    private bool _disposed;
    private readonly bool _ownsWeights = true;

    /// <summary>
    /// Creates a lightweight worker instance sharing all pre-loaded neural network weights,
    /// but with its own independent accumulator stack for thread-safe concurrent search.
    /// </summary>
    public IIncrementalEvaluator CreateWorkerInstance()
    {
        return new MultiTierNnueEvaluator(this);
    }

    private MultiTierNnueEvaluator(MultiTierNnueEvaluator parent)
    {
        _ownsWeights = false;
        _moeWeights = parent._moeWeights;
        _openWeights = parent._openWeights;
        _closedWeights = parent._closedWeights;
        _endgameWeights = parent._endgameWeights;
        _queenlessWeights = parent._queenlessWeights;
        _imbalanceWeights = parent._imbalanceWeights;
        _attackWeights = parent._attackWeights;
        _activeFullWeights = parent._activeFullWeights;
        _fallbackEvaluator = parent._fallbackEvaluator;
        UseNnue = parent.UseNnue;
        NnueDirectory = parent.NnueDirectory;
        Tablebase = parent.Tablebase;
        ActiveRootExpert = parent.ActiveRootExpert;
        Routing = parent.Routing;
    }


    public bool UseNnue { get; set; } = true;
    public string NnueDirectory { get; set; } = "./networks";
    public SyzygyTablebase? Tablebase { get; set; }

    /// <summary>
    /// Tunable expert routing (rule order and thresholds). Immutable; replace the whole instance to change it.
    /// </summary>
    public ExpertRouting Routing { get => _routing; set => _routing = value ?? ExpertRouting.Default; }
    private volatile ExpertRouting _routing = ExpertRouting.Default;


    public NnueExpertType ActiveRootExpert { get; private set; } = NnueExpertType.Closed;

    public bool IsUnifiedMoE => _moeWeights != null;
    public bool HasMoeModel => _moeWeights != null;
    public bool HasOpenModel => _moeWeights != null || _openWeights != null;
    public bool HasClosedModel => _moeWeights != null || _closedWeights != null;
    public bool HasEndgameModel => _moeWeights != null || _endgameWeights != null;
    public bool HasQueenlessModel => _moeWeights != null || _queenlessWeights != null;
    public bool HasImbalanceModel => _moeWeights != null || _imbalanceWeights != null;
    public bool HasAttackModel => _moeWeights != null || _attackWeights != null;

    public bool IsActive => UseNnue && (
        _moeWeights != null ||
        _openWeights != null ||
        _closedWeights != null ||
        _endgameWeights != null ||
        _queenlessWeights != null ||
        _imbalanceWeights != null ||
        _attackWeights != null);

    public NnueWeights? ActiveWeights => _moeWeights ?? _activeFullWeights ?? _openWeights ?? _closedWeights ?? _endgameWeights ?? _queenlessWeights ?? _imbalanceWeights ?? _attackWeights;


    public MultiTierNnueEvaluator(string? networkDir = null)
    {
        _fallbackEvaluator = new DefaultEvaluator();
        string dirToLoad = !string.IsNullOrWhiteSpace(networkDir) ? networkDir : NnueDirectory;
        LoadFromDirectory(dirToLoad);
    }

    /// <summary>
    /// Loads the unified ArchMoE network (<c>anak.nnue</c>) or legacy expert <c>.nnue</c> networks from the specified directory,
    /// falling back to embedded resources for any models not found on disk.
    /// Returns true if at least one model was successfully loaded.
    /// </summary>
    public bool LoadFromDirectory(string directoryPath)
    {
        string? resolvedDir = ResolveDirectory(directoryPath);
        if (resolvedDir != null)
        {
            NnueDirectory = resolvedDir;

            // Prefer unified Shared-FT Multi-Expert network (anak.nnue / moe.nnue)
            string anakPath = Path.Combine(resolvedDir, "anak.nnue");
            if (File.Exists(anakPath))
            {
                NnueWeights? loaded = NnueWeights.LoadFromFile(anakPath);
                if (loaded != null)
                {
                    if (loaded.IsMoE)
                    {
                        _moeWeights?.Dispose();
                        _moeWeights = loaded;
                    }
                    else if (_openWeights == null && _closedWeights == null)
                    {
                        _openWeights = loaded;
                        _closedWeights = loaded;
                    }
                    else
                    {
                        loaded.Dispose();
                    }
                }
            }

            if (_moeWeights == null)
            {
                TryLoadModel(resolvedDir, "open.nnue", ref _openWeights);
                TryLoadModel(resolvedDir, "closed.nnue", ref _closedWeights);
                TryLoadModel(resolvedDir, "endgame.nnue", ref _endgameWeights);
                TryLoadModel(resolvedDir, "queenless.nnue", ref _queenlessWeights);
                TryLoadModel(resolvedDir, "imbalance.nnue", ref _imbalanceWeights);
                TryLoadModel(resolvedDir, "attack.nnue", ref _attackWeights);
            }
        }

        // Fall back to embedded network weights
        if (_moeWeights == null)
        {
            NnueWeights? embeddedMoE = null;
            TryLoadEmbeddedModel("anak.nnue", ref embeddedMoE);
            if (embeddedMoE != null)
            {
                if (embeddedMoE.IsMoE)
                {
                    _moeWeights = embeddedMoE;
                }
                else
                {
                    embeddedMoE.Dispose();
                }
            }
        }

        if (_moeWeights == null)
        {
            TryLoadEmbeddedModel("open.nnue", ref _openWeights);
            TryLoadEmbeddedModel("closed.nnue", ref _closedWeights);
            TryLoadEmbeddedModel("endgame.nnue", ref _endgameWeights);
            TryLoadEmbeddedModel("queenless.nnue", ref _queenlessWeights);
            TryLoadEmbeddedModel("imbalance.nnue", ref _imbalanceWeights);
            TryLoadEmbeddedModel("attack.nnue", ref _attackWeights);
        }

        if (resolvedDir == null && IsActive)
        {
            NnueDirectory = "<embedded>";
        }

        // Active model will be locked at root when search begins via SetRootTopology / InitSearch
        _activeFullWeights = _moeWeights;

        return IsActive;
    }

    private static void TryLoadEmbeddedModel(string fileName, ref NnueWeights? targetSlot)
    {
        if (targetSlot != null)
        {
            return;
        }

        System.Reflection.Assembly assembly = typeof(MultiTierNnueEvaluator).Assembly;
        string resourceName = $"networks.{fileName}";
        using Stream? stream = assembly.GetManifestResourceStream(resourceName)
            ?? FindResourceByFileName(assembly, fileName);

        if (stream != null)
        {
            NnueWeights? loaded = NnueWeights.LoadFromStream(stream);
            if (loaded != null)
            {
                targetSlot = loaded;
            }
        }
    }

    private static Stream? FindResourceByFileName(System.Reflection.Assembly assembly, string fileName)
    {
        string[] names = assembly.GetManifestResourceNames();
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return assembly.GetManifestResourceStream(names[i]);
            }
        }
        return null;
    }

    private static void TryLoadModel(string resolvedDir, string fileName, ref NnueWeights? targetSlot)
    {
        string path = Path.Combine(resolvedDir, fileName);
        if (File.Exists(path))
        {
            NnueWeights? loaded = NnueWeights.LoadFromFile(path);
            if (loaded != null)
            {
                targetSlot?.Dispose();
                targetSlot = loaded;
            }
        }
    }

    private static string? ResolveDirectory(string directoryPath)
    {
        bool isDefault = string.IsNullOrWhiteSpace(directoryPath) || directoryPath == "./networks" || directoryPath == "networks";

        if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
        {
            return Path.GetFullPath(directoryPath);
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            string candidatePath = Path.Combine(baseDir, directoryPath);
            if (Directory.Exists(candidatePath))
            {
                return Path.GetFullPath(candidatePath);
            }
        }

        if (!isDefault)
        {
            // Explicit non-default path specified by caller does not exist
            return null;
        }

        string candidate = Path.Combine(baseDir, "networks");
        if (Directory.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        string? parent = baseDir;
        for (int i = 0; i < 6 && parent != null; i++)
        {
            candidate = Path.Combine(parent, "networks");
            if (Directory.Exists(candidate) && (File.Exists(Path.Combine(candidate, "anak.nnue")) || File.Exists(Path.Combine(candidate, "open.nnue")) || File.Exists(Path.Combine(candidate, "closed.nnue"))))
            {
                return Path.GetFullPath(candidate);
            }

            candidate = Path.Combine(parent, "training", "networks");
            if (Directory.Exists(candidate) && (File.Exists(Path.Combine(candidate, "anak.nnue")) || File.Exists(Path.Combine(candidate, "open.nnue")) || File.Exists(Path.Combine(candidate, "closed.nnue"))))
            {
                return Path.GetFullPath(candidate);
            }

            parent = Directory.GetParent(parent)?.FullName;
        }

        return null;
    }

    /// <summary>
    /// Classifies the board position into one of the 6 specialized NNUE experts.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NnueExpertType ClassifyBoard(in Board board)
    {
        return PawnTopology.ClassifyExpert(in board, _routing);
    }

    /// <summary>
    /// Returns the loaded <see cref="NnueWeights"/> for the requested <paramref name="expert"/>,
    /// or gracefully falls back to the open/closed weights (according to board openness) if that expert file is not present.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NnueWeights? GetExpertWeights(NnueExpertType expert, in Board board)
    {
        NnueWeights? specific = expert switch
        {
            NnueExpertType.Open => _openWeights,
            NnueExpertType.Closed => _closedWeights,
            NnueExpertType.Endgame => _endgameWeights,
            NnueExpertType.Queenless => _queenlessWeights,
            NnueExpertType.Imbalance => _imbalanceWeights,
            NnueExpertType.Attack => _attackWeights,
            _ => null
        };

        if (specific != null)
        {
            return specific;
        }

        // Graceful fallback to Open/Closed based on pawn structure openness
        bool isOpen = PawnTopology.IsOpen(in board, _routing.OpenThreshold);
        NnueWeights? topologyFallback = isOpen ? _openWeights : _closedWeights;
        return topologyFallback
            ?? _openWeights
            ?? _closedWeights
            ?? _endgameWeights
            ?? _queenlessWeights
            ?? _imbalanceWeights
            ?? _attackWeights;
    }

    /// <summary>
    /// Evaluates the root board's topology and phase once and records the active root expert.
    /// In unified ArchMoE mode, locks <see cref="_activeFullWeights"/> to the shared-FT MoE weights.
    /// </summary>
    public void SetRootTopology(in Board rootBoard)
    {
        ActiveRootExpert = PawnTopology.ClassifyExpert(in rootBoard, _routing);
        _activeFullWeights = _moeWeights ?? GetExpertWeights(ActiveRootExpert, in rootBoard);
    }

    /// <summary>
    /// Initializes accumulator stack at root position before starting search simulations.
    /// </summary>
    public void InitSearch(in Board rootBoard)
    {
        SetRootTopology(in rootBoard);
        NnueWeights? weights = _activeFullWeights;
        if (weights != null)
        {
            int dim = weights.HiddenDim;
            fixed (short* wPtr = _stackWhite, bPtr = _stackBlack)
            {
                NnueAccumulator.ComputeFull(in rootBoard, weights, wPtr, bPtr);
            }
            _stackKeys[0] = rootBoard.ZobristKey;
            _stackDepth = 0;
        }
        else
        {
            _stackDepth = -1;
        }
    }

    /// <summary>
    /// Pushes a move along the search descent path and updates the accumulator incrementally.
    /// </summary>
    public void PushMove(Move move, Piece movedPiece, Piece capturedPiece, in Board boardAfter)
    {
        if (_stackDepth < 0 || _stackDepth >= 254) return;
        NnueWeights? weights = _activeFullWeights;
        if (weights == null) return;

        int dim = weights.HiddenDim;
        int curOffset = _stackDepth * dim;
        int nextOffset = (_stackDepth + 1) * dim;

        fixed (short* wPtr = _stackWhite, bPtr = _stackBlack)
        {
            short* curW = wPtr + curOffset;
            short* curB = bPtr + curOffset;
            short* nextW = wPtr + nextOffset;
            short* nextB = bPtr + nextOffset;

            NnueAccumulator.UpdateMove(move, movedPiece, capturedPiece, weights, curW, curB, nextW, nextB, in boardAfter);
        }

        _stackDepth++;
        _stackKeys[_stackDepth] = boardAfter.ZobristKey;
    }

    /// <summary>
    /// Pushes a null move along the search descent path (pieces unchanged, only key and depth updated).
    /// </summary>
    public void PushNullMove(in Board boardAfter)
    {
        if (_stackDepth < 0 || _stackDepth >= 254) return;
        NnueWeights? weights = _activeFullWeights;
        if (weights == null) return;

        int dim = weights.HiddenDim;
        int curOffset = _stackDepth * dim;
        int nextOffset = (_stackDepth + 1) * dim;

        fixed (short* wPtr = _stackWhite, bPtr = _stackBlack)
        {
            short* curW = wPtr + curOffset;
            short* curB = bPtr + curOffset;
            short* nextW = wPtr + nextOffset;
            short* nextB = bPtr + nextOffset;

            CopyAccumulator(curW, nextW, dim);
            CopyAccumulator(curB, nextB, dim);
        }

        _stackDepth++;
        _stackKeys[_stackDepth] = boardAfter.ZobristKey;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyAccumulator(short* src, short* dst, int count)
    {
        if (Avx2.IsSupported && count == 512)
        {
            for (int i = 0; i < 512; i += 64)
            {
                Vector256<short> v0 = Vector256.Load(src + i);
                Vector256<short> v1 = Vector256.Load(src + i + 16);
                Vector256<short> v2 = Vector256.Load(src + i + 32);
                Vector256<short> v3 = Vector256.Load(src + i + 48);

                v0.Store(dst + i);
                v1.Store(dst + i + 16);
                v2.Store(dst + i + 32);
                v3.Store(dst + i + 48);
            }
        }
        else if (Avx2.IsSupported && (count & 15) == 0)
        {
            for (int i = 0; i < count; i += 16)
            {
                Vector256.Load(src + i).Store(dst + i);
            }
        }
        else
        {
            Buffer.MemoryCopy(src, dst, (long)count * sizeof(short), (long)count * sizeof(short));
        }
    }

    /// <summary>
    /// Rolls back the internal accumulator stack depth.
    /// </summary>
    public void RollbackTo(int depth)
    {
        if (_stackDepth >= 0)
        {
            _stackDepth = Math.Clamp(depth, 0, 254);
        }
    }

    /// <summary>
    /// Evaluates the board position using a specific <see cref="NnueWeights"/> instance.
    /// Zero heap allocations via stackalloc accumulators.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int EvaluateWithWeights(in Board board, NnueWeights weights)
    {
        int dim = weights.HiddenDim;
        short* whiteAcc = stackalloc short[dim];
        short* blackAcc = stackalloc short[dim];

        NnueAccumulator.ComputeFull(in board, weights, whiteAcc, blackAcc);
        return NnueInference.Evaluate(whiteAcc, blackAcc, weights, board.SideToMove);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int EvaluateMoEForBoard(short* whiteAcc, short* blackAcc, NnueWeights moeWeights, in Board board)
    {
        NnueExpertType leafExpert = PawnTopology.ClassifyExpert(in board, _routing, out int opennessPct);
        if (leafExpert is NnueExpertType.Open or NnueExpertType.Closed)
        {
            int thresholdPct = _routing.OpenThresholdPct;
            int low = thresholdPct - 8;
            int high = thresholdPct + 8;
            if (opennessPct > low && opennessPct < high)
            {
                int alpha256 = (opennessPct - low) << 4;
                return NnueInference.EvaluateMoEBlended(
                    whiteAcc,
                    blackAcc,
                    moeWeights,
                    board.SideToMove,
                    (int)NnueExpertType.Closed,
                    (int)NnueExpertType.Open,
                    alpha256);
            }
        }

        return NnueInference.EvaluateMoE(whiteAcc, blackAcc, moeWeights, board.SideToMove, (int)leafExpert);
    }

    public Value Evaluate(in Board board)
    {
        if (!UseNnue)
        {
            return _fallbackEvaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);
        }

        // Fast incremental evaluation path during active search
        if (_stackDepth >= 0 && _activeFullWeights != null && _stackKeys[_stackDepth] == board.ZobristKey)
        {
            NnueWeights weights = _activeFullWeights;
            int dim = weights.HiddenDim;
            int offset = _stackDepth * dim;
            fixed (short* wPtr = _stackWhite, bPtr = _stackBlack)
            {
                short* wAcc = wPtr + offset;
                short* bAcc = bPtr + offset;
                int scoreCp = ReferenceEquals(weights, _moeWeights)
                    ? EvaluateMoEForBoard(wAcc, bAcc, weights, in board)
                    : NnueInference.Evaluate(wAcc, bAcc, weights, board.SideToMove);
                return new Value(scoreCp);
            }
        }

        // Unified Shared-FT MoE full-eval fallback (outside active search or after stack miss)
        if (_moeWeights != null)
        {
            int dim = _moeWeights.HiddenDim;
            short* whiteAcc = stackalloc short[dim];
            short* blackAcc = stackalloc short[dim];
            NnueAccumulator.ComputeFull(in board, _moeWeights, whiteAcc, blackAcc);
            return new Value(EvaluateMoEForBoard(whiteAcc, blackAcc, _moeWeights, in board));
        }

        // Legacy Root Expert Consistency: always evaluate with _activeFullWeights if non-null
        if (_activeFullWeights != null)
        {
            int cp = EvaluateWithWeights(in board, _activeFullWeights);
            return new Value(cp);
        }

        NnueExpertType leafExpert = PawnTopology.ClassifyExpert(in board, _routing);
        NnueWeights? selectedWeights = GetExpertWeights(leafExpert, in board);

        if (selectedWeights == null)
        {
            return _fallbackEvaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);
        }

        // Smooth Boundary Interpolation when routing through Open/Closed middlegame experts outside active search
        bool usesOpenOrClosed = leafExpert == NnueExpertType.Open
            || leafExpert == NnueExpertType.Closed
            || ReferenceEquals(selectedWeights, _openWeights)
            || ReferenceEquals(selectedWeights, _closedWeights);

        if (usesOpenOrClosed
            && _openWeights != null
            && _closedWeights != null
            && !ReferenceEquals(_openWeights, _closedWeights))
        {
            float openness = PawnTopology.CalculateOpenness(in board);
            float blendLow = _routing.OpenThreshold - 0.08f;
            float blendHigh = _routing.OpenThreshold + 0.08f;
            if (openness > blendLow && openness < blendHigh)
            {
                int closedCp = EvaluateWithWeights(in board, _closedWeights);
                int openCp = EvaluateWithWeights(in board, _openWeights);
                float alpha = (openness - blendLow) / 0.16f;
                int blendedCp = (int)MathF.Round(((1.0f - alpha) * closedCp) + (alpha * openCp));
                return new Value(blendedCp);
            }
        }

        int fullCp = EvaluateWithWeights(in board, selectedWeights);
        return new Value(fullCp);
    }

    /// <summary>
    /// Evaluates the board position using dynamic multi-expert routing and smooth Open/Closed boundary interpolation.
    /// Uses incremental pre-accumulated state if called within an active search descent.
    /// Falls back gracefully to DefaultEvaluator if NNUE is disabled or weights are absent.
    /// </summary>
    public Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves) => Evaluate(in board);

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsWeights)
            {
                _moeWeights?.Dispose();
                _openWeights?.Dispose();
                if (!ReferenceEquals(_closedWeights, _openWeights))
                {
                    _closedWeights?.Dispose();
                }
                _endgameWeights?.Dispose();
                _queenlessWeights?.Dispose();
                _imbalanceWeights?.Dispose();
                _attackWeights?.Dispose();
            }
            _disposed = true;
        }
    }
}
