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
/// Multi-Expert &amp; Multi-Tier NNUE evaluator integrating 6 specialized full experts
/// (Open, Closed, Endgame, Queenless, Imbalance, Attack), smooth Open/Closed boundary interpolation,
/// Micro-NNUE tactical watchdog and volatility networks, Syzygy tablebases, and fallback to DefaultEvaluator.
/// </summary>
public sealed unsafe class MultiTierNnueEvaluator : IMultiTierEvaluator, IIncrementalEvaluator, IDisposable
{
    private NnueWeights? _openWeights;
    private NnueWeights? _closedWeights;
    private NnueWeights? _endgameWeights;
    private NnueWeights? _queenlessWeights;
    private NnueWeights? _imbalanceWeights;
    private NnueWeights? _attackWeights;
    private NnueWeights? _microWeights;
    private NnueWeights? _volatilityWeights;
    private NnueWeights? _activeFullWeights;
    private readonly DefaultEvaluator _fallbackEvaluator;
    private readonly short[] _stackWhite = new short[256 * 512];
    private readonly short[] _stackBlack = new short[256 * 512];
    private readonly short[] _stackMicroWhite = new short[256 * 16];
    private readonly short[] _stackMicroBlack = new short[256 * 16];
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
        _openWeights = parent._openWeights;
        _closedWeights = parent._closedWeights;
        _endgameWeights = parent._endgameWeights;
        _queenlessWeights = parent._queenlessWeights;
        _imbalanceWeights = parent._imbalanceWeights;
        _attackWeights = parent._attackWeights;
        _microWeights = parent._microWeights;
        _volatilityWeights = parent._volatilityWeights;
        _activeFullWeights = parent._activeFullWeights;
        _fallbackEvaluator = parent._fallbackEvaluator;
        UseNnue = parent.UseNnue;
        NnueDirectory = parent.NnueDirectory;
        BlunderWatchdog = parent.BlunderWatchdog;
        Tablebase = parent.Tablebase;
        ActiveRootExpert = parent.ActiveRootExpert;
        Routing = parent.Routing;
    }


    public bool UseNnue { get; set; } = true;
    public string NnueDirectory { get; set; } = "./networks";
    public bool BlunderWatchdog { get; set; } = true;
    public SyzygyTablebase? Tablebase { get; set; }

    /// <summary>
    /// Tunable expert routing (rule order and thresholds). Immutable; replace the whole instance to change it.
    /// </summary>
    public ExpertRouting Routing { get => _routing; set => _routing = value ?? ExpertRouting.Default; }
    private volatile ExpertRouting _routing = ExpertRouting.Default;


    public NnueExpertType ActiveRootExpert { get; private set; } = NnueExpertType.Closed;

    public bool HasOpenModel => _openWeights != null;
    public bool HasClosedModel => _closedWeights != null;
    public bool HasEndgameModel => _endgameWeights != null;
    public bool HasQueenlessModel => _queenlessWeights != null;
    public bool HasImbalanceModel => _imbalanceWeights != null;
    public bool HasAttackModel => _attackWeights != null;
    public bool HasMicroModel => _microWeights != null;
    public bool HasVolatilityModel => _volatilityWeights != null;

    public bool IsActive => UseNnue && (
        _openWeights != null ||
        _closedWeights != null ||
        _endgameWeights != null ||
        _queenlessWeights != null ||
        _imbalanceWeights != null ||
        _attackWeights != null);

    public NnueWeights? ActiveWeights => _activeFullWeights ?? _openWeights ?? _closedWeights ?? _endgameWeights ?? _queenlessWeights ?? _imbalanceWeights ?? _attackWeights;


    public MultiTierNnueEvaluator(string? networkDir = null)
    {
        _fallbackEvaluator = new DefaultEvaluator();
        string dirToLoad = !string.IsNullOrWhiteSpace(networkDir) ? networkDir : NnueDirectory;
        LoadFromDirectory(dirToLoad);
    }

    /// <summary>
    /// Loads all available expert and micro .nnue networks from the specified directory.
    /// Returns true if at least one model was successfully loaded.
    /// </summary>
    public bool LoadFromDirectory(string directoryPath)
    {
        string? resolvedDir = ResolveDirectory(directoryPath);
        if (resolvedDir == null)
        {
            return false;
        }

        NnueDirectory = resolvedDir;

        TryLoadModel(resolvedDir, "open.nnue", ref _openWeights);
        TryLoadModel(resolvedDir, "closed.nnue", ref _closedWeights);
        TryLoadModel(resolvedDir, "endgame.nnue", ref _endgameWeights);
        TryLoadModel(resolvedDir, "queenless.nnue", ref _queenlessWeights);
        TryLoadModel(resolvedDir, "imbalance.nnue", ref _imbalanceWeights);
        TryLoadModel(resolvedDir, "attack.nnue", ref _attackWeights);

        // If a generic anak.nnue exists, load it as fallback for both open/closed
        string genericPath = Path.Combine(resolvedDir, "anak.nnue");
        if (_openWeights == null && _closedWeights == null && File.Exists(genericPath))
        {
            _openWeights = NnueWeights.LoadFromFile(genericPath);
            _closedWeights = _openWeights;
        }

        TryLoadModel(resolvedDir, "micro.nnue", ref _microWeights);
        TryLoadModel(resolvedDir, "volatility.nnue", ref _volatilityWeights);

        // Active model will be locked at root when search begins via SetRootTopology / InitSearch
        _activeFullWeights = null;

        return IsActive || _microWeights != null || _volatilityWeights != null;
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
        if (Directory.Exists(directoryPath))
        {
            return Path.GetFullPath(directoryPath);
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string candidate = Path.Combine(baseDir, directoryPath);
        if (Directory.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        candidate = Path.Combine(baseDir, "networks");
        if (Directory.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        string? parent = baseDir;
        for (int i = 0; i < 6 && parent != null; i++)
        {
            candidate = Path.Combine(parent, "networks");
            if (Directory.Exists(candidate) && (File.Exists(Path.Combine(candidate, "open.nnue")) || File.Exists(Path.Combine(candidate, "closed.nnue"))))
            {
                return Path.GetFullPath(candidate);
            }

            candidate = Path.Combine(parent, "training", "networks");
            if (Directory.Exists(candidate) && (File.Exists(Path.Combine(candidate, "open.nnue")) || File.Exists(Path.Combine(candidate, "closed.nnue"))))
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
    /// </summary>
    public void SetRootTopology(in Board rootBoard)
    {
        ActiveRootExpert = PawnTopology.ClassifyExpert(in rootBoard, _routing);
        _activeFullWeights = GetExpertWeights(ActiveRootExpert, in rootBoard);
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
            if (_microWeights != null)
            {
                fixed (short* mwPtr = _stackMicroWhite, mbPtr = _stackMicroBlack)
                {
                    NnueAccumulator.ComputeFull(in rootBoard, _microWeights, mwPtr, mbPtr);
                }
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

            CopyAccumulator(curW, nextW, dim);
            CopyAccumulator(curB, nextB, dim);

            NnueAccumulator.UpdateMove(move, movedPiece, capturedPiece, weights, nextW, nextB, in boardAfter);
        }

        if (_microWeights != null)
        {
            int curM = _stackDepth * 16;
            int nextM = (_stackDepth + 1) * 16;
            fixed (short* mwPtr = _stackMicroWhite, mbPtr = _stackMicroBlack)
            {
                short* curMW = mwPtr + curM;
                short* curMB = mbPtr + curM;
                short* nextMW = mwPtr + nextM;
                short* nextMB = mbPtr + nextM;

                if (Avx2.IsSupported)
                {
                    Vector256.Load(curMW).Store(nextMW);
                    Vector256.Load(curMB).Store(nextMB);
                }
                else
                {
                    Buffer.MemoryCopy(curMW, nextMW, 16 * sizeof(short), 16 * sizeof(short));
                    Buffer.MemoryCopy(curMB, nextMB, 16 * sizeof(short), 16 * sizeof(short));
                }

                NnueAccumulator.UpdateMove(move, movedPiece, capturedPiece, _microWeights, nextMW, nextMB, in boardAfter);
            }
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

        if (_microWeights != null)
        {
            int curM = _stackDepth * 16;
            int nextM = (_stackDepth + 1) * 16;
            fixed (short* mwPtr = _stackMicroWhite, mbPtr = _stackMicroBlack)
            {
                if (Avx2.IsSupported)
                {
                    Vector256.Load(mwPtr + curM).Store(mwPtr + nextM);
                    Vector256.Load(mbPtr + curM).Store(mbPtr + nextM);
                }
                else
                {
                    Buffer.MemoryCopy(mwPtr + curM, mwPtr + nextM, 16 * sizeof(short), 16 * sizeof(short));
                    Buffer.MemoryCopy(mbPtr + curM, mbPtr + nextM, 16 * sizeof(short), 16 * sizeof(short));
                }
            }
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

    /// <summary>
    /// Evaluates the board position using dynamic multi-expert routing and smooth Open/Closed boundary interpolation.
    /// Uses incremental pre-accumulated state if called within an active search descent.
    /// Falls back gracefully to DefaultEvaluator if NNUE is disabled or weights are absent.
    /// </summary>
    public Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves)
    {
        if (!UseNnue)
        {
            return _fallbackEvaluator.Evaluate(in board, legalMoves);
        }

        // Fast incremental evaluation path during active search (1 SIMD dot product)
        if (_stackDepth >= 0 && _activeFullWeights != null && _stackKeys[_stackDepth] == board.ZobristKey)
        {
            NnueWeights weights = _activeFullWeights;
            int dim = weights.HiddenDim;
            int offset = _stackDepth * dim;
            fixed (short* wPtr = _stackWhite, bPtr = _stackBlack)
            {
                short* wAcc = wPtr + offset;
                short* bAcc = bPtr + offset;
                int scoreCp = NnueInference.Evaluate(wAcc, bAcc, weights, board.SideToMove);
                return new Value(scoreCp);
            }
        }

        // Root Expert Consistency: always evaluate with _activeFullWeights if non-null (no thrashing across experts or blending during search)
        if (_activeFullWeights != null)
        {
            int cp = EvaluateWithWeights(in board, _activeFullWeights);
            return new Value(cp);
        }

        NnueExpertType leafExpert = PawnTopology.ClassifyExpert(in board, _routing);
        NnueWeights? selectedWeights = GetExpertWeights(leafExpert, in board);

        if (selectedWeights == null)
        {
            return _fallbackEvaluator.Evaluate(in board, legalMoves);
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
    /// Evaluates the board position using the ultra-fast Micro-NNUE model (1 SIMD register)
    /// for fast blunder and trap checking.
    /// </summary>
    public Value EvaluateMicro(in Board board)
    {
        if (!UseNnue || _microWeights == null)
        {
            return Evaluate(in board, ReadOnlySpan<Move>.Empty);
        }

        // Fast incremental path during active search (1 single SIMD dot product)
        if (_stackDepth >= 0 && _stackKeys[_stackDepth] == board.ZobristKey)
        {
            int offset = _stackDepth * 16;
            fixed (short* mwPtr = _stackMicroWhite, mbPtr = _stackMicroBlack)
            {
                short* wAcc = mwPtr + offset;
                short* bAcc = mbPtr + offset;
                short* ourAcc = board.SideToMove == Color.White ? wAcc : bAcc;
                short* theirAcc = board.SideToMove == Color.White ? bAcc : wAcc;
                int score = NnueInference.EvaluateMicro(ourAcc, theirAcc, _microWeights);
                return new Value(score);
            }
        }

        const int dim = NnueConstants.MicroHiddenDim;
        short* whiteAcc = stackalloc short[dim];
        short* blackAcc = stackalloc short[dim];

        NnueAccumulator.ComputeFull(in board, _microWeights, whiteAcc, blackAcc);
        short* oAcc = board.SideToMove == Color.White ? whiteAcc : blackAcc;
        short* tAcc = board.SideToMove == Color.White ? blackAcc : whiteAcc;

        int cp = NnueInference.EvaluateMicro(oAcc, tAcc, _microWeights);
        return new Value(cp);
    }

    /// <summary>
    /// Evaluates the tactical sharpness / volatility of the board position in [0, 1000].
    /// Uses volatility.nnue when loaded, or a fast zero-allocation bitboard tension/attack estimator as fallback.
    /// </summary>
    public Value EvaluateVolatility(in Board board)
    {
        if (UseNnue && _volatilityWeights != null)
        {
            int dim = _volatilityWeights.HiddenDim;
            short* whiteAcc = stackalloc short[dim];
            short* blackAcc = stackalloc short[dim];

            NnueAccumulator.ComputeFull(in board, _volatilityWeights, whiteAcc, blackAcc);
            int cp = NnueInference.Evaluate(whiteAcc, blackAcc, _volatilityWeights, board.SideToMove);
            return new Value(Math.Clamp(Math.Abs(cp), 0, 1000));
        }

        return new Value(ComputeBitboardVolatility(in board));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeBitboardVolatility(in Board board)
    {
        ulong whitePawns = board.White & board.Pawns;
        ulong blackPawns = board.Black & board.Pawns;

        // 1. Pawn tension (mutually attacking pawns)
        ulong whitePawnAttacks = ((whitePawns << 7) & ~Bitboard.FileH) | ((whitePawns << 9) & ~Bitboard.FileA);
        ulong blackPawnAttacks = ((blackPawns >> 9) & ~Bitboard.FileH) | ((blackPawns >> 7) & ~Bitboard.FileA);
        int pawnTension = BitOperations.PopCount(whitePawnAttacks & blackPawns);

        // 2. Non-pawn pieces attacked by enemy pawns
        ulong whiteNonPawns = board.White & ~board.Pawns & ~board.Kings;
        ulong blackNonPawns = board.Black & ~board.Pawns & ~board.Kings;
        int pawnThreatenedPieces = BitOperations.PopCount(blackPawnAttacks & whiteNonPawns)
            + BitOperations.PopCount(whitePawnAttacks & blackNonPawns);

        // 3. Opposite-side castling / mutual king attack factor
        int kingAttackBonus = PawnTopology.IsOppositeSideCastling(in board) ? 120 : 0;

        // 4. In-check bonus
        int checkBonus = board.IsInCheck() ? 180 : 0;

        int score = (pawnTension * 60) + (pawnThreatenedPieces * 110) + kingAttackBonus + checkBonus;
        return Math.Clamp(score, 0, 1000);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsWeights)
            {
                _openWeights?.Dispose();
                if (!ReferenceEquals(_closedWeights, _openWeights))
                {
                    _closedWeights?.Dispose();
                }
                _endgameWeights?.Dispose();
                _queenlessWeights?.Dispose();
                _imbalanceWeights?.Dispose();
                _attackWeights?.Dispose();
                _microWeights?.Dispose();
                _volatilityWeights?.Dispose();
            }
            _disposed = true;
        }
    }
}
