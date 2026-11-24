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
    private readonly MultiTierNnueEvaluator? _parent;
    private volatile NnueWeights? _weights;
    private NnueWeights? _searchWeights;
    private readonly DefaultEvaluator _fallbackEvaluator;
    private short* _stackWhite;
    private short* _stackBlack;
    private int _stackCapacityDim;
    private readonly ulong[] _stackKeys = new ulong[256];
    private readonly int[] _stackSlot = new int[256];
    private readonly uint[] _stackKingInfo = new uint[256];
    private readonly NnueFinnyTable _finnyTable = new();
    private int _stackDepth = -1;
    private bool _disposed;
    private readonly bool _ownsWeights = true;
    private volatile bool _useNnue = true;
    private string _nnueDirectory = "./networks";
    private SyzygyTablebase? _tablebase;
    private volatile ExpertRouting _routing = ExpertRouting.Default;

    private NnueWeights? CurrentWeights => _parent != null ? _parent._weights : _weights;

    public bool UseNnue
    {
        get => _parent != null ? _parent.UseNnue : _useNnue;
        set { if (_parent != null) _parent.UseNnue = value; else _useNnue = value; }
    }
    public string NnueDirectory
    {
        get => _parent != null ? _parent.NnueDirectory : _nnueDirectory;
        set { if (_parent != null) _parent.NnueDirectory = value; else _nnueDirectory = value; }
    }
    public SyzygyTablebase? Tablebase
    {
        get => _parent != null ? _parent.Tablebase : _tablebase;
        set { if (_parent != null) _parent.Tablebase = value; else _tablebase = value; }
    }
    public ExpertRouting Routing
    {
        get => _parent != null ? _parent.Routing : _routing;
        set
        {
            ExpertRouting next = value ?? ExpertRouting.Default;
            if (_parent != null) _parent.Routing = next;
            else _routing = next;
        }
    }

    public NnueExpertType ActiveRootExpert { get; private set; } = NnueExpertType.Closed;

    public bool IsUnifiedMoE => CurrentWeights != null;
    public bool HasMoeModel => CurrentWeights != null;
    public bool HasOpenModel => CurrentWeights != null;
    public bool HasClosedModel => CurrentWeights != null;
    public bool HasEndgameModel => CurrentWeights != null;
    public bool HasQueenlessModel => CurrentWeights != null;
    public bool HasImbalanceModel => CurrentWeights != null;
    public bool HasAttackModel => CurrentWeights != null;
    public bool IsActive => UseNnue && CurrentWeights != null;
    public NnueWeights? ActiveWeights => CurrentWeights;

    public MultiTierNnueEvaluator(string? networkDir = null)
    {
        _fallbackEvaluator = new DefaultEvaluator();
        string dirToLoad = !string.IsNullOrWhiteSpace(networkDir) ? networkDir : NnueDirectory;
        LoadFromDirectory(dirToLoad);
        EnsureStackCapacity(CurrentWeights?.HiddenDim ?? NnueConstants.V2DefaultHiddenDim);
    }

    private MultiTierNnueEvaluator(MultiTierNnueEvaluator parent)
    {
        _parent = parent;
        _ownsWeights = false;
        _fallbackEvaluator = parent._fallbackEvaluator;
        ActiveRootExpert = parent.ActiveRootExpert;
        EnsureStackCapacity(CurrentWeights?.HiddenDim ?? NnueConstants.V2DefaultHiddenDim);
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
        if (_parent != null)
        {
            return _parent.LoadFromDirectory(directoryPath);
        }

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
                    NnueWeights? oldWeights = _weights;
                    _weights = loaded;
                    _searchWeights = null;
                    _stackDepth = -1;
                    if (_ownsWeights) oldWeights?.Dispose();
                }
            }
        }

        if (_weights == null)
        {
            NnueWeights? embedded = null;
            TryLoadEmbeddedModel("anak.nnue", ref embedded);
            if (embedded != null)
            {
                _weights = embedded;
            }
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
    public NnueExpertType ClassifyBoard(in Board board) => PawnTopology.ClassifyExpert(in board, Routing);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NnueWeights? GetExpertWeights(NnueExpertType expert, in Board board) => CurrentWeights;

    public void SetRootTopology(in Board rootBoard)
    {
        ActiveRootExpert = PawnTopology.ClassifyExpert(in rootBoard, Routing);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint PackKingInfo(in Board board, int buckets)
    {
        if (buckets == 1) return 0u;
        int wKingSq = NnueAccumulator.GetWhiteKingSquare(in board);
        int bKingSq = NnueAccumulator.GetBlackKingSquare(in board);
        uint wBucket = (uint)NnueAccumulator.GetKingBucket((Square)wKingSq);
        uint bBucket = (uint)NnueAccumulator.GetKingBucket((Square)(bKingSq ^ 56));
        uint wFlip = (wKingSq & 4) != 0 ? 7u : 0u;
        uint bFlip = (bKingSq & 4) != 0 ? 7u : 0u;
        return wBucket | (wFlip << 5) | (bBucket << 8) | (bFlip << 13);
    }

    public void InitSearch(in Board rootBoard)
    {
        SetRootTopology(in rootBoard);
        NnueWeights? weights = CurrentWeights;
        _searchWeights = weights;
        if (weights != null)
        {
            EnsureStackCapacity(weights.HiddenDim);
            _finnyTable.RefreshWhite(in rootBoard, weights, _stackWhite);
            _finnyTable.RefreshBlack(in rootBoard, weights, _stackBlack);
            _stackKeys[0] = rootBoard.ZobristKey;
            _stackSlot[0] = 0;
            _stackKingInfo[0] = PackKingInfo(in rootBoard, weights.Buckets);
            _stackDepth = 0;
        }
        else
        {
            _stackDepth = -1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void PushMove(Move move, Piece movedPiece, Piece capturedPiece, in Board boardAfter)
    {
        int depth = _stackDepth;
        NnueWeights? weights = _searchWeights;
        if ((uint)depth >= 254u || weights == null) return;

        int dim = weights.HiddenDim;
        int curSlot = _stackSlot[depth];
        int nextDepth = depth + 1;
        short* curW = _stackWhite + (curSlot * dim);
        short* curB = _stackBlack + (curSlot * dim);
        short* nextW = _stackWhite + (nextDepth * dim);
        short* nextB = _stackBlack + (nextDepth * dim);

        if (movedPiece.Type() != PieceType.King)
        {
            uint info = _stackKingInfo[depth];
            _stackKingInfo[nextDepth] = info;
            NnueAccumulator.UpdateNonKingMove(
                move, movedPiece, capturedPiece, weights,
                (int)(info & 31u), (int)((info >> 5) & 7u),
                (int)((info >> 8) & 31u), (int)((info >> 13) & 7u),
                curW, curB, nextW, nextB);
        }
        else
        {
            NnueAccumulator.UpdateMove(move, movedPiece, capturedPiece, weights, curW, curB, nextW, nextB, in boardAfter, _finnyTable);
            _stackKingInfo[nextDepth] = PackKingInfo(in boardAfter, weights.Buckets);
        }

        _stackSlot[nextDepth] = nextDepth;
        _stackDepth = nextDepth;
        _stackKeys[nextDepth] = boardAfter.ZobristKey;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushNullMove(in Board boardAfter)
    {
        int depth = _stackDepth;
        if ((uint)depth >= 254u || _searchWeights == null) return;
        int nextDepth = depth + 1;
        _stackSlot[nextDepth] = _stackSlot[depth];
        _stackKingInfo[nextDepth] = _stackKingInfo[depth];
        _stackDepth = nextDepth;
        _stackKeys[nextDepth] = boardAfter.ZobristKey;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public Value Evaluate(in Board board)
    {
        int depth = _stackDepth;
        NnueWeights? weights = depth >= 0 ? _searchWeights : CurrentWeights;
        if (!UseNnue || weights == null)
        {
            return _fallbackEvaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty);
        }

        if (depth >= 0 && _stackKeys[depth] == board.ZobristKey)
        {
            int offset = _stackSlot[depth] * weights.HiddenDim;
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
            if (_ownsWeights)
            {
                _weights?.Dispose();
                _weights = null;
            }
            _disposed = true;
        }
    }
}
