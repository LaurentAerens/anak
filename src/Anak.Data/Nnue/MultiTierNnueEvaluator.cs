using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;
using Anak.Data.Evaluation;
using Anak.Data.Syzygy;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;

namespace Anak.Data.Nnue;

/// <summary>
/// Unified Anak v1.2+ NNUE evaluator (<c>ArchV2 = 0x0004</c>, <c>anak.nnue</c>) with
/// 64-byte aligned incremental accumulator stack, Finny Table king-bucket cache,
/// zero-copy null move, and 50-move rule drawishness scaling.
/// Falls back gracefully to <see cref="DefaultEvaluator"/> if NNUE is disabled or weights are absent.
/// </summary>
[SkipLocalsInit]
public sealed unsafe class MultiTierNnueEvaluator : IMultiTierEvaluator, IIncrementalEvaluator, IDisposable
{
    private NnueWeights? _weights;
    private readonly DefaultEvaluator _fallbackEvaluator;
    private short* _stackWhite;
    private short* _stackBlack;
    private int _stackCapacityDim;
    private readonly ulong[] _stackKeys = new ulong[256];
    private readonly int[] _stackSlot = new int[256];
    private readonly NnueFinnyTable _finnyTable = new();
    private int _stackDepth = -1;
    private bool _disposed;
    private readonly bool _ownsWeights = true;

    public bool UseNnue { get; set; } = true;
    public string NnueDirectory { get; set; } = "./networks";
    public SyzygyTablebase? Tablebase { get; set; }
    public ExpertRouting Routing { get => _routing; set => _routing = value ?? ExpertRouting.Default; }
    private volatile ExpertRouting _routing = ExpertRouting.Default;

    public NnueExpertType ActiveRootExpert { get; private set; } = NnueExpertType.Closed;

    public bool IsUnifiedMoE => _weights != null;
    public bool HasMoeModel => _weights != null;
    public bool HasOpenModel => _weights != null;
    public bool HasClosedModel => _weights != null;
    public bool HasEndgameModel => _weights != null;
    public bool HasQueenlessModel => _weights != null;
    public bool HasImbalanceModel => _weights != null;
    public bool HasAttackModel => _weights != null;
    public bool IsActive => UseNnue && _weights != null;
    public NnueWeights? ActiveWeights => _weights;

    public MultiTierNnueEvaluator(string? networkDir = null)
    {
        _fallbackEvaluator = new DefaultEvaluator();
        string dirToLoad = !string.IsNullOrWhiteSpace(networkDir) ? networkDir : NnueDirectory;
        LoadFromDirectory(dirToLoad);
        EnsureStackCapacity(_weights?.HiddenDim ?? NnueConstants.V2DefaultHiddenDim);
    }

    private MultiTierNnueEvaluator(MultiTierNnueEvaluator parent)
    {
        _ownsWeights = false;
        _weights = parent._weights;
        _fallbackEvaluator = parent._fallbackEvaluator;
        UseNnue = parent.UseNnue;
        NnueDirectory = parent.NnueDirectory;
        Tablebase = parent.Tablebase;
        ActiveRootExpert = parent.ActiveRootExpert;
        Routing = parent.Routing;
        EnsureStackCapacity(_weights?.HiddenDim ?? NnueConstants.V2DefaultHiddenDim);
    }

    public IIncrementalEvaluator CreateWorkerInstance() => new MultiTierNnueEvaluator(this);

    private void EnsureStackCapacity(int minDim)
    {
        int targetDim = Math.Max(NnueConstants.V2DefaultHiddenDim, minDim);
        if (_stackWhite != null && _stackCapacityDim >= targetDim) return;

        if (_stackWhite != null) NativeMemory.AlignedFree(_stackWhite);
        if (_stackBlack != null) NativeMemory.AlignedFree(_stackBlack);

        nuint bytes = (nuint)(256 * targetDim * sizeof(short));
        _stackWhite = (short*)NativeMemory.AlignedAlloc(bytes, 64);
        _stackBlack = (short*)NativeMemory.AlignedAlloc(bytes, 64);
        _stackCapacityDim = targetDim;
    }

    /// <summary>
    /// Loads <c>anak.nnue</c> (ArchV2) from the specified directory, falling back to the embedded resource.
    /// </summary>
    public bool LoadFromDirectory(string directoryPath)
    {
        string? resolvedDir = ResolveDirectory(directoryPath);
        if (resolvedDir != null)
        {
            NnueDirectory = resolvedDir;
            string anakPath = Path.Combine(resolvedDir, "anak.nnue");
            if (File.Exists(anakPath))
            {
                NnueWeights? loaded = NnueWeights.LoadFromFile(anakPath);
                if (loaded != null)
                {
                    if (_ownsWeights) _weights?.Dispose();
                    _weights = loaded;
                }
            }
        }

        if (_weights == null)
        {
            TryLoadEmbeddedModel("anak.nnue", ref _weights);
        }

        if (resolvedDir == null && IsActive)
        {
            NnueDirectory = "<embedded>";
        }

        EnsureStackCapacity(_weights?.HiddenDim ?? NnueConstants.V2DefaultHiddenDim);
        return IsActive;
    }

    private static void TryLoadEmbeddedModel(string fileName, ref NnueWeights? targetSlot)
    {
        if (targetSlot != null) return;

        System.Reflection.Assembly assembly = typeof(MultiTierNnueEvaluator).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream($"networks.{fileName}")
            ?? FindResourceByFileName(assembly, fileName);

        if (stream != null)
        {
            NnueWeights? loaded = NnueWeights.LoadFromStream(stream);
            if (loaded != null) targetSlot = loaded;
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
            if (Directory.Exists(candidatePath)) return Path.GetFullPath(candidatePath);
        }

        if (!isDefault) return null;

        string candidate = Path.Combine(baseDir, "networks");
        if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);

        string? parent = baseDir;
        for (int i = 0; i < 6 && parent != null; i++)
        {
            candidate = Path.Combine(parent, "networks");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "anak.nnue")))
            {
                return Path.GetFullPath(candidate);
            }
            parent = Directory.GetParent(parent)?.FullName;
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NnueExpertType ClassifyBoard(in Board board) => PawnTopology.ClassifyExpert(in board, _routing);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NnueWeights? GetExpertWeights(NnueExpertType expert, in Board board) => _weights;

    public void SetRootTopology(in Board rootBoard)
    {
        ActiveRootExpert = PawnTopology.ClassifyExpert(in rootBoard, _routing);
    }

    public void InitSearch(in Board rootBoard)
    {
        SetRootTopology(in rootBoard);
        NnueWeights? weights = _weights;
        if (weights != null)
        {
            EnsureStackCapacity(weights.HiddenDim);
            _finnyTable.RefreshWhite(in rootBoard, weights, _stackWhite);
            _finnyTable.RefreshBlack(in rootBoard, weights, _stackBlack);
            _stackKeys[0] = rootBoard.ZobristKey;
            _stackSlot[0] = 0;
            _stackDepth = 0;
        }
        else
        {
            _stackDepth = -1;
        }
    }

    public void PushMove(Move move, Piece movedPiece, Piece capturedPiece, in Board boardAfter)
    {
        if (_stackDepth < 0 || _stackDepth >= 254) return;
        NnueWeights? weights = _weights;
        if (weights == null) return;

        int dim = weights.HiddenDim;
        int curSlot = _stackSlot[_stackDepth];
        int nextDepth = _stackDepth + 1;
        short* curW = _stackWhite + (curSlot * dim);
        short* curB = _stackBlack + (curSlot * dim);
        short* nextW = _stackWhite + (nextDepth * dim);
        short* nextB = _stackBlack + (nextDepth * dim);

        NnueAccumulator.UpdateMove(move, movedPiece, capturedPiece, weights, curW, curB, nextW, nextB, in boardAfter, _finnyTable);

        _stackSlot[nextDepth] = nextDepth;
        _stackDepth = nextDepth;
        _stackKeys[nextDepth] = boardAfter.ZobristKey;
    }

    public void PushNullMove(in Board boardAfter)
    {
        if (_stackDepth < 0 || _stackDepth >= 254 || _weights == null) return;
        int nextDepth = _stackDepth + 1;
        _stackSlot[nextDepth] = _stackSlot[_stackDepth];
        _stackDepth = nextDepth;
        _stackKeys[nextDepth] = boardAfter.ZobristKey;
    }

    public void RollbackTo(int depth)
    {
        if (_stackDepth >= 0) _stackDepth = Math.Clamp(depth, 0, 254);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int EvaluateWithWeights(in Board board, NnueWeights weights)
    {
        int dim = weights.HiddenDim;
        short* whiteAcc = stackalloc short[dim];
        short* blackAcc = stackalloc short[dim];
        NnueAccumulator.ComputeFull(in board, weights, whiteAcc, blackAcc);
        int bucket = NnueInference.GetV2OutputBucket(in board, weights.OutputBuckets);
        return NnueInference.EvaluateV2(whiteAcc, blackAcc, weights, board.SideToMove, bucket);
    }

    public Value Evaluate(in Board board)
    {
        NnueWeights? weights = _weights;
        if (!UseNnue || weights == null)
        {
            return _fallbackEvaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);
        }

        if (_stackDepth >= 0 && _stackKeys[_stackDepth] == board.ZobristKey)
        {
            int offset = _stackSlot[_stackDepth] * weights.HiddenDim;
            int bucket = NnueInference.GetV2OutputBucket(in board, weights.OutputBuckets);
            int scoreCp = NnueInference.EvaluateV2(_stackWhite + offset, _stackBlack + offset, weights, board.SideToMove, bucket);
            return new Value(ScaleByHalfmoveClock(scoreCp, board.HalfmoveClock));
        }

        int fullCp = EvaluateWithWeights(in board, weights);
        return new Value(ScaleByHalfmoveClock(fullCp, board.HalfmoveClock));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScaleByHalfmoveClock(int scoreCp, int halfmoveClock) =>
        halfmoveClock > 0 ? (scoreCp * Math.Max(0, 200 - halfmoveClock)) / 200 : scoreCp;

    public Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves) => Evaluate(in board);

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_stackWhite != null) { NativeMemory.AlignedFree(_stackWhite); _stackWhite = null; }
            if (_stackBlack != null) { NativeMemory.AlignedFree(_stackBlack); _stackBlack = null; }
            _finnyTable.Dispose();
            if (_ownsWeights) _weights?.Dispose();
            _disposed = true;
        }
    }
}
