using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;
using Anak.Data.Nnue;

namespace Zanak.Search;

/// <summary>
/// Zanak's NNUE evaluator. Reuses the ArchV2 weights loaded by Anak's <see cref="MultiTierNnueEvaluator"/>
/// (read-only, shared by all workers) with its own 64-byte aligned accumulator stack and Finny table.
/// </summary>
[SkipLocalsInit]
public sealed unsafe class ZanakNnueEvaluator : IMultiTierEvaluator, IIncrementalEvaluator, IDisposable
{
    private const int MaxStackDepth = 254;

    private readonly MultiTierNnueEvaluator _networks;
    private readonly bool _ownsNetworks;
    private short* _stackWhite;
    private short* _stackBlack;
    private int _stackCapacityDim;
    private readonly ulong[] _stackKeys = new ulong[256];
    private readonly int[] _stackSlot = new int[256];
    private readonly NnueFinnyTable _finnyTable = new();
    private NnueWeights? _rootWeights;
    private int _stackDepth = -1;
    private bool _disposed;

    public ZanakNnueEvaluator(string? networkDir = null) : this(new MultiTierNnueEvaluator(networkDir), ownsNetworks: true)
    {
    }

    private ZanakNnueEvaluator(MultiTierNnueEvaluator networks, bool ownsNetworks)
    {
        _networks = networks;
        _ownsNetworks = ownsNetworks;
        _rootWeights = networks.ActiveWeights;
        EnsureStackCapacity(_rootWeights?.HiddenDim ?? NnueConstants.V2DefaultHiddenDim);
    }

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

    public MultiTierNnueEvaluator Networks => _networks;
    public bool IsActive => _networks.IsActive;
    public string NnueDirectory => _networks.NnueDirectory;
    public NnueExpertType ActiveRootExpert { get; private set; } = NnueExpertType.Closed;
    public bool IsUnifiedMoE => _networks.IsUnifiedMoE;
    public bool HasMoeModel => _networks.HasMoeModel;

    public IIncrementalEvaluator CreateWorkerInstance() =>
        new ZanakNnueEvaluator(_networks, ownsNetworks: false)
        {
            ActiveRootExpert = ActiveRootExpert,
            _rootWeights = _rootWeights
        };

    public NnueExpertType ClassifyBoard(in Board board) => _networks.ClassifyBoard(in board);

    public void SetRootTopology(in Board rootBoard)
    {
        ActiveRootExpert = _networks.ClassifyBoard(in rootBoard);
        _rootWeights = _networks.ActiveWeights;
        _stackDepth = -1;
    }

    public void ResetAccumulatorStack(in Board board)
    {
        NnueWeights? weights = _rootWeights;
        if (weights == null)
        {
            _stackDepth = -1;
            return;
        }

        EnsureStackCapacity(weights.HiddenDim);
        _finnyTable.RefreshWhite(in board, weights, _stackWhite);
        _finnyTable.RefreshBlack(in board, weights, _stackBlack);
        _stackKeys[0] = board.ZobristKey;
        _stackSlot[0] = 0;
        _stackDepth = 0;
    }

    public void InitSearch(in Board rootBoard)
    {
        SetRootTopology(in rootBoard);
        ResetAccumulatorStack(in rootBoard);
    }

    public void PushMove(Move move, Piece movedPiece, Piece capturedPiece, in Board boardAfter)
    {
        NnueWeights? weights = _rootWeights;
        if (_stackDepth < 0 || _stackDepth >= MaxStackDepth || weights == null) return;

        int dim = weights.HiddenDim;
        int curSlot = _stackSlot[_stackDepth];
        int nextDepth = _stackDepth + 1;
        NnueAccumulator.UpdateMove(
            move, movedPiece, capturedPiece, weights,
            _stackWhite + (curSlot * dim), _stackBlack + (curSlot * dim),
            _stackWhite + (nextDepth * dim), _stackBlack + (nextDepth * dim),
            in boardAfter, _finnyTable);

        _stackSlot[nextDepth] = nextDepth;
        _stackDepth = nextDepth;
        _stackKeys[nextDepth] = boardAfter.ZobristKey;
    }

    public void PushNullMove(in Board boardAfter)
    {
        if (_stackDepth < 0 || _stackDepth >= MaxStackDepth || _rootWeights == null) return;
        int nextDepth = _stackDepth + 1;
        _stackSlot[nextDepth] = _stackSlot[_stackDepth];
        _stackDepth = nextDepth;
        _stackKeys[nextDepth] = boardAfter.ZobristKey;
    }

    public void RollbackTo(int depth)
    {
        if (_stackDepth >= 0) _stackDepth = Math.Clamp(depth, 0, MaxStackDepth);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EvaluateAccumulators(short* whiteAcc, short* blackAcc, NnueWeights weights, in Board board)
    {
        int bucket = NnueInference.GetV2OutputBucket(in board, weights.OutputBuckets);
        int scoreCp = NnueInference.EvaluateV2(whiteAcc, blackAcc, weights, board.SideToMove, bucket);
        return board.HalfmoveClock > 0 ? (scoreCp * Math.Max(0, 200 - board.HalfmoveClock)) / 200 : scoreCp;
    }

    public Value Evaluate(in Board board)
    {
        NnueWeights? weights = _rootWeights;
        if (!_networks.UseNnue || weights == null) return _networks.Evaluate(in board);

        if (_stackDepth >= 0 && _stackKeys[_stackDepth] == board.ZobristKey)
        {
            int offset = _stackSlot[_stackDepth] * weights.HiddenDim;
            return new Value(EvaluateAccumulators(_stackWhite + offset, _stackBlack + offset, weights, in board));
        }

        int dim = weights.HiddenDim;
        short* whiteAcc = stackalloc short[dim];
        short* blackAcc = stackalloc short[dim];
        NnueAccumulator.ComputeFull(in board, weights, whiteAcc, blackAcc);
        return new Value(EvaluateAccumulators(whiteAcc, blackAcc, weights, in board));
    }

    public Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves) =>
        _networks.UseNnue ? Evaluate(in board) : _networks.Evaluate(in board, legalMoves);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_stackWhite != null) { NativeMemory.AlignedFree(_stackWhite); _stackWhite = null; }
        if (_stackBlack != null) { NativeMemory.AlignedFree(_stackBlack); _stackBlack = null; }
        _finnyTable.Dispose();
        if (_ownsNetworks) _networks.Dispose();
    }
}
