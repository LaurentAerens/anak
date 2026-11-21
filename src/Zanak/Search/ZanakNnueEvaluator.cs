using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Anak.Core;
using Anak.Data.Nnue;

namespace Zanak.Search;

/// <summary>
/// Zanak's NNUE evaluator. It reuses the networks loaded by Anak's <see cref="MultiTierNnueEvaluator"/> (read-only,
/// shared by all workers) but owns its root expert and accumulator stack, so Anak's evaluator stays untouched.
/// Unlike <see cref="InitSearch"/>, <see cref="ResetAccumulatorStack"/> seeds the stack at any position without
/// re-selecting the expert: spines and agent lines that start below the root keep the root's network.
/// </summary>
[SkipLocalsInit]
public sealed unsafe class ZanakNnueEvaluator : IMultiTierEvaluator, IIncrementalEvaluator, IDisposable
{
    private const int MaxHiddenDim = 512;
    private const int MaxStackDepth = 254;

    private readonly MultiTierNnueEvaluator _networks;
    private readonly bool _ownsNetworks;
    private readonly short[] _stackWhite = new short[256 * MaxHiddenDim];
    private readonly short[] _stackBlack = new short[256 * MaxHiddenDim];
    private readonly ulong[] _stackKeys = new ulong[256];
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
        NnueExpertType expert = _networks.ClassifyBoard(in rootBoard);
        NnueWeights? weights = _networks.IsUnifiedMoE
            ? _networks.ActiveWeights
            : _networks.GetExpertWeights(expert, in rootBoard);
        ActiveRootExpert = expert;
        _rootWeights = weights != null && weights.HiddenDim <= MaxHiddenDim ? weights : null;
        _stackDepth = -1; // any accumulated state belongs to the previous expert
    }

    /// <summary>Seeds the accumulator stack at <paramref name="board"/> with the fixed root expert (no-op without one).</summary>
    public void ResetAccumulatorStack(in Board board)
    {
        NnueWeights? weights = _rootWeights;
        if (weights == null)
        {
            _stackDepth = -1;
            return;
        }

        fixed (short* wPtr = _stackWhite, bPtr = _stackBlack)
        {
            NnueAccumulator.ComputeFull(in board, weights, wPtr, bPtr);
        }
        _stackKeys[0] = board.ZobristKey;
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
        fixed (short* wPtr = _stackWhite, bPtr = _stackBlack)
        {
            int cur = _stackDepth * dim;
            int next = cur + dim;
            NnueAccumulator.UpdateMove(move, movedPiece, capturedPiece, weights,
                wPtr + cur, bPtr + cur, wPtr + next, bPtr + next, in boardAfter);
        }
        _stackDepth++;
        _stackKeys[_stackDepth] = boardAfter.ZobristKey;
    }

    public void PushNullMove(in Board boardAfter)
    {
        NnueWeights? weights = _rootWeights;
        if (_stackDepth < 0 || _stackDepth >= MaxStackDepth || weights == null) return;

        int dim = weights.HiddenDim;
        int curOffset = _stackDepth * dim;
        int nextOffset = (_stackDepth + 1) * dim;
        fixed (short* wPtr = _stackWhite, bPtr = _stackBlack)
        {
            CopyAccumulator(wPtr + curOffset, wPtr + nextOffset, dim);
            CopyAccumulator(bPtr + curOffset, bPtr + nextOffset, dim);
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

    public void RollbackTo(int depth)
    {
        if (_stackDepth >= 0) _stackDepth = Math.Clamp(depth, 0, MaxStackDepth);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int EvaluateAccumulators(short* whiteAcc, short* blackAcc, NnueWeights weights, in Board board)
    {
        int scoreCp;
        if (weights.IsMoE)
        {
            ExpertRouting routing = _networks.Routing;
            NnueExpertType leafExpert = PawnTopology.ClassifyExpert(in board, routing);
            if (leafExpert is NnueExpertType.Open or NnueExpertType.Closed)
            {
                int thresholdPct = (int)MathF.Round(routing.OpenThreshold * 100.0f);
                int low = thresholdPct - 8;
                int high = thresholdPct + 8;
                int opennessPct = PawnTopology.CalculateOpennessPercent(in board);
                if (opennessPct > low && opennessPct < high)
                {
                    int alpha256 = ((opennessPct - low) * 256) / 16;
                    scoreCp = NnueInference.EvaluateMoEBlended(
                        whiteAcc,
                        blackAcc,
                        weights,
                        board.SideToMove,
                        (int)NnueExpertType.Closed,
                        (int)NnueExpertType.Open,
                        alpha256);
                    return board.HalfmoveClock > 0 ? (scoreCp * Math.Max(0, 200 - board.HalfmoveClock)) / 200 : scoreCp;
                }
            }

            scoreCp = NnueInference.EvaluateMoE(whiteAcc, blackAcc, weights, board.SideToMove, (int)leafExpert);
        }
        else
        {
            scoreCp = NnueInference.Evaluate(whiteAcc, blackAcc, weights, board.SideToMove);
        }

        return board.HalfmoveClock > 0 ? (scoreCp * Math.Max(0, 200 - board.HalfmoveClock)) / 200 : scoreCp;
    }

    public Value Evaluate(in Board board)
    {
        NnueWeights? weights = _rootWeights;
        if (!_networks.UseNnue || weights == null) return _networks.Evaluate(in board);

        if (_stackDepth >= 0 && _stackKeys[_stackDepth] == board.ZobristKey)
        {
            int offset = _stackDepth * weights.HiddenDim;
            fixed (short* wPtr = _stackWhite, bPtr = _stackBlack)
            {
                return new Value(EvaluateAccumulators(wPtr + offset, bPtr + offset, weights, in board));
            }
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
        if (_ownsNetworks) _networks.Dispose();
    }
}
