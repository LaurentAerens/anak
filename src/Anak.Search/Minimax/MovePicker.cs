using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

[InlineArray(256)]
public struct ScoreBuffer
{
    private int _element0;
}

[InlineArray(256)]
public struct ShortBuffer256
{
    private short _element0;
}

[InlineArray(32)]
public struct MoveBuffer32
{
    private Move _element0;
}

[InlineArray(128)]
public struct IndexBuffer128
{
    private byte _element0;
}

[InlineArray(4)]
public struct PickedBitmask
{
    private ulong _element0;
}

public enum MovePickerStage : byte
{
    TtMove = 0,
    GoodCaptures = 1,
    Killer0 = 2,
    Killer1 = 3,
    CounterMove = 4,
    ContinuationMoves = 5,
    QuietMoves = 6,
    BadCaptures = 7,
    Done = 8
}

/// <summary>
/// Zero-allocation staged move picker.
/// Orders moves progressively across 6 distinct stages:
/// 1. TT Move (Hash move)
/// 2. Good Captures (SEE >= 0, sorted by MVV-LVA score + CaptureHistory)
/// 3. Killer 0 and Killer 1
/// 4. Countermove & 1-ply/2-ply Continuation History moves
/// 5. Quiet moves (sorted by Butterfly History)
/// 6. Bad Captures (SEE < 0)
/// </summary>
[SkipLocalsInit]
public ref struct MovePicker
{
    private readonly ref readonly Board _board;
    private readonly Move _ttMove;
    private readonly Move _killer0;
    private readonly Move _killer1;
    private readonly Move _counterMove;
    private readonly HistoryTable? _history;
    private readonly CaptureHistory? _captureHistory;
    private readonly ContinuationHistory? _continuationHistory;
    private readonly Piece _prev1Piece;
    private readonly Square _prev1To;
    private readonly Piece _prev2Piece;
    private readonly Square _prev2To;
    private readonly int _prev1Offset;
    private readonly int _prev2Offset;

    private MoveList _legalMoves;
    private ScoreBuffer _scores;
    private ShortBuffer256 _seeScores;
    private IndexBuffer128 _goodCaptureIndices;
    private int _goodCaptureCount;
    private IndexBuffer128 _badCaptureIndices;
    private int _badCaptureCount;
    private IndexBuffer128 _quietIndices;
    private int _quietCount;
    private MovePickerStage _stage;
    private bool _capturesScored;
    private bool _contScored;
    private bool _quietsScored;
    private bool _capturesGenerated;
    private bool _allMovesGenerated;
    private bool _ttMovePicked;
    private MoveBuffer32 _playedGoodCaptures;
    private int _playedGoodCapturesCount;

    /// <summary>
    /// Gets the stage in which the current move was picked.
    /// </summary>
    public MovePickerStage CurrentStage { get; private set; }

    /// <summary>
    /// Gets the Static Exchange Evaluation (SEE) score of the current picked move, if evaluated.
    /// </summary>
    public int CurrentSee { get; private set; }

    // 256-bit bitmask tracking picked moves
    private PickedBitmask _picked;

    public MovePicker(
        in Board board,
        Move ttMove,
        Move killer0,
        Move killer1,
        Move counterMove,
        HistoryTable? history,
        CaptureHistory? captureHistory,
        ContinuationHistory? continuationHistory,
        Piece prev1Piece = Piece.None,
        Square prev1To = Square.None,
        Piece prev2Piece = Piece.None,
        Square prev2To = Square.None)
    {
        _board = ref board;
        _ttMove = ttMove;
        _killer0 = killer0;
        _killer1 = killer1;
        _counterMove = counterMove;
        _history = history;
        _captureHistory = captureHistory;
        _continuationHistory = continuationHistory;
        _prev1Piece = prev1Piece;
        _prev1To = prev1To;
        _prev2Piece = prev2Piece;
        _prev2To = prev2To;
        _prev1Offset = ContinuationHistory.GetBaseOffset(prev1Piece, prev1To);
        _prev2Offset = ContinuationHistory.GetBaseOffset(prev2Piece, prev2To);

        Unsafe.SkipInit(out _legalMoves);
        _legalMoves.Clear();
        _capturesGenerated = false;
        _allMovesGenerated = false;
        _ttMovePicked = false;
        _playedGoodCapturesCount = 0;

        Unsafe.SkipInit(out _scores);
        Unsafe.SkipInit(out _seeScores);
        Unsafe.SkipInit(out _goodCaptureIndices);
        _goodCaptureCount = 0;
        Unsafe.SkipInit(out _badCaptureIndices);
        _badCaptureCount = 0;
        Unsafe.SkipInit(out _quietIndices);
        _quietCount = 0;
        _stage = MovePickerStage.TtMove;
        _capturesScored = false;
        _contScored = false;
        _quietsScored = false;
        _picked = default;
        CurrentStage = MovePickerStage.TtMove;
        CurrentSee = int.MinValue;
    }

    public MovePicker(
        in Board board,
        Move ttMove = default,
        Move killer0 = default,
        Move killer1 = default,
        HistoryTable? history = null)
        : this(in board, ttMove, killer0, killer1, Move.Null, history, null, null)
    {
    }

    public int MoveCount
    {
        get
        {
            EnsureAllMovesGenerated();
            return _legalMoves.Count;
        }
    }

    public readonly MovePickerStage Stage => _stage;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapturesGenerated()
    {
        if (_capturesGenerated || _allMovesGenerated) return;
        if (_board.IsInCheck())
        {
            EnsureAllMovesGenerated();
            return;
        }
        _capturesGenerated = true;
        _legalMoves.Clear();
        MoveGenerator.GenerateLegalCaptures(in _board, ref _legalMoves);

        if (_ttMovePicked && !_ttMove.IsNull)
        {
            int ttIdx = FindMove(_ttMove);
            if (ttIdx >= 0) MarkPicked(ttIdx);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureAllMovesGenerated()
    {
        if (_allMovesGenerated) return;
        _allMovesGenerated = true;
        _legalMoves.Clear();
        MoveGenerator.GenerateLegalMoves(in _board, ref _legalMoves);
        _picked = default;

        if (_ttMovePicked && !_ttMove.IsNull)
        {
            int ttIdx = FindMove(_ttMove);
            if (ttIdx >= 0) MarkPicked(ttIdx);
        }
        for (int p = 0; p < _playedGoodCapturesCount; p++)
        {
            int capIdx = FindMove(_playedGoodCaptures[p]);
            if (capIdx >= 0) MarkPicked(capIdx);
        }
        _capturesScored = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool IsPicked(int index)
    {
        int word = index >> 6;
        return (uint)word < 4u ? (_picked[word] & (1UL << (index & 63))) != 0 : true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkPicked(int index)
    {
        int word = index >> 6;
        if ((uint)word < 4u)
        {
            _picked[word] |= 1UL << (index & 63);
        }
    }

    /// <summary>
    /// Gets the next move in the staged move ordering.
    /// Returns true if a move was picked, false if no more moves.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool Next(out Move move)
    {
        while (_stage != MovePickerStage.Done)
        {
            switch (_stage)
            {
                case MovePickerStage.TtMove:
                    _stage = MovePickerStage.GoodCaptures;
                    if (!_ttMove.IsNull && _board.IsLegal(_ttMove))
                    {
                        _ttMovePicked = true;
                        if (_allMovesGenerated || _capturesGenerated)
                        {
                            int ttIdx = FindMove(_ttMove);
                            if (ttIdx >= 0) MarkPicked(ttIdx);
                        }
                        CurrentStage = MovePickerStage.TtMove;
                        CurrentSee = int.MinValue;
                        move = _ttMove;
                        return true;
                    }
                    break;

                case MovePickerStage.GoodCaptures:
                    EnsureCapturesGenerated();
                    if (!_capturesScored)
                    {
                        ScoreCaptures();
                        _capturesScored = true;
                    }

                    int bestCapIdx = PickBestGoodCapture();
                    if (bestCapIdx >= 0)
                    {
                        MarkPicked(bestCapIdx);
                        Move pickedMove = _legalMoves[bestCapIdx];
                        if (_playedGoodCapturesCount < 32)
                        {
                            _playedGoodCaptures[_playedGoodCapturesCount++] = pickedMove;
                        }
                        CurrentStage = MovePickerStage.GoodCaptures;
                        CurrentSee = _seeScores[bestCapIdx];
                        move = pickedMove;
                        return true;
                    }

                    _stage = MovePickerStage.Killer0;
                    break;

                case MovePickerStage.Killer0:
                    _stage = MovePickerStage.Killer1;
                    if (!_killer0.IsNull && !_killer0.IsCapture && !_killer0.IsPromotion && _killer0 != _ttMove)
                    {
                        EnsureAllMovesGenerated();
                        int k0Idx = FindMove(_killer0);
                        if (k0Idx >= 0 && !IsPicked(k0Idx))
                        {
                            MarkPicked(k0Idx);
                            CurrentStage = MovePickerStage.Killer0;
                            CurrentSee = int.MinValue;
                            move = _killer0;
                            return true;
                        }
                    }
                    break;

                case MovePickerStage.Killer1:
                    _stage = MovePickerStage.CounterMove;
                    if (!_killer1.IsNull && !_killer1.IsCapture && !_killer1.IsPromotion && _killer1 != _ttMove && _killer1 != _killer0)
                    {
                        EnsureAllMovesGenerated();
                        int k1Idx = FindMove(_killer1);
                        if (k1Idx >= 0 && !IsPicked(k1Idx))
                        {
                            MarkPicked(k1Idx);
                            CurrentStage = MovePickerStage.Killer1;
                            CurrentSee = int.MinValue;
                            move = _killer1;
                            return true;
                        }
                    }
                    break;

                case MovePickerStage.CounterMove:
                    _stage = MovePickerStage.ContinuationMoves;
                    if (!_counterMove.IsNull && !_counterMove.IsCapture && !_counterMove.IsPromotion &&
                        _counterMove != _ttMove && _counterMove != _killer0 && _counterMove != _killer1)
                    {
                        EnsureAllMovesGenerated();
                        int cmIdx = FindMove(_counterMove);
                        if (cmIdx >= 0 && !IsPicked(cmIdx))
                        {
                            MarkPicked(cmIdx);
                            CurrentStage = MovePickerStage.CounterMove;
                            CurrentSee = int.MinValue;
                            move = _counterMove;
                            return true;
                        }
                    }
                    break;

                case MovePickerStage.ContinuationMoves:
                    EnsureAllMovesGenerated();
                    int bestContIdx = PickBestContinuationMove();
                    if (bestContIdx >= 0)
                    {
                        MarkPicked(bestContIdx);
                        CurrentStage = MovePickerStage.ContinuationMoves;
                        CurrentSee = int.MinValue;
                        move = _legalMoves[bestContIdx];
                        return true;
                    }

                    _stage = MovePickerStage.QuietMoves;
                    break;

                case MovePickerStage.QuietMoves:
                    EnsureAllMovesGenerated();
                    if (!_quietsScored)
                    {
                        ScoreQuiets();
                        _quietsScored = true;
                    }

                    int bestQuietIdx = PickBestQuiet();
                    if (bestQuietIdx >= 0)
                    {
                        MarkPicked(bestQuietIdx);
                        CurrentStage = MovePickerStage.QuietMoves;
                        CurrentSee = int.MinValue;
                        move = _legalMoves[bestQuietIdx];
                        return true;
                    }

                    _stage = MovePickerStage.BadCaptures;
                    break;

                case MovePickerStage.BadCaptures:
                    EnsureAllMovesGenerated();
                    if (!_capturesScored)
                    {
                        ScoreCaptures();
                        _capturesScored = true;
                    }

                    int bestBadCapIdx = PickBestBadCapture();
                    if (bestBadCapIdx >= 0)
                    {
                        MarkPicked(bestBadCapIdx);
                        CurrentStage = MovePickerStage.BadCaptures;
                        CurrentSee = _seeScores[bestBadCapIdx];
                        move = _legalMoves[bestBadCapIdx];
                        return true;
                    }

                    _stage = MovePickerStage.Done;
                    break;
            }
        }

        CurrentStage = MovePickerStage.Done;
        CurrentSee = int.MinValue;
        move = Move.Null;
        return false;
    }

    /// <summary>
    /// Gets the next move, returning <see cref="Move.Null"/> if no more moves remain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Move Next()
    {
        return Next(out Move move) ? move : Move.Null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly int FindMove(Move move)
    {
        for (int i = 0; i < _legalMoves.Count; i++)
        {
            if (_legalMoves[i] == move) return i;
        }
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ScoreCaptures()
    {
        _goodCaptureCount = 0;
        _badCaptureCount = 0;
        for (int i = 0; i < _legalMoves.Count; i++)
        {
            Move m = _legalMoves[i];
            if (!m.IsCapture && !m.IsPromotion) continue;

            Piece moved = _board.GetPieceAt(m.From);
            Piece captured = m.Type == MoveType.EnPassant
                ? PieceExtensions.Create(_board.SideToMove.Opponent(), PieceType.Pawn)
                : _board.GetPieceAt(m.To);

            int see = StaticExchangeEvaluator.Evaluate(in _board, m);
            _seeScores[i] = (short)see;
            if (see >= 0)
            {
                int score = GetMvvLva(moved, captured, m);
                if (_captureHistory != null)
                {
                    score += _captureHistory.GetScore(moved, m.To, captured) / 16;
                }
                _scores[i] = Math.Max(0, score);
                _goodCaptureIndices[_goodCaptureCount++] = (byte)i;
            }
            else
            {
                // Bad capture: negative score offset
                _scores[i] = -1_000_000 + see;
                _badCaptureIndices[_badCaptureCount++] = (byte)i;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly int PickBestGoodCapture()
    {
        int bestIdx = -1;
        int bestScore = -1;

        for (int j = 0; j < _goodCaptureCount; j++)
        {
            int i = _goodCaptureIndices[j];
            if (IsPicked(i)) continue;

            int score = _scores[i];
            if (score >= 0 && score > bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int PickBestContinuationMove()
    {
        if (_continuationHistory == null || (_prev1Piece == Piece.None && _prev2Piece == Piece.None))
        {
            return -1;
        }

        if (!_contScored)
        {
            _contScored = true;
            _quietCount = 0;
            for (int i = 0; i < _legalMoves.Count; i++)
            {
                if (IsPicked(i)) continue;
                Move m = _legalMoves[i];
                if (m.IsCapture || m.IsPromotion) continue;

                Piece moved = _board.GetPieceAt(m.From);
                int contScore = _continuationHistory.Get1PlyAt(_prev1Offset, moved, m.To) +
                                _continuationHistory.Get2PlyAt(_prev2Offset, moved, m.To);
                if (contScore > 0)
                {
                    _scores[i] = contScore;
                    _quietIndices[_quietCount++] = (byte)i;
                }
            }
        }

        int bestIdx = -1;
        int bestScore = 0; // Only positive continuation scores

        for (int j = 0; j < _quietCount; j++)
        {
            int i = _quietIndices[j];
            if (IsPicked(i)) continue;

            int contScore = _scores[i];
            if (contScore > bestScore)
            {
                bestScore = contScore;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ScoreQuiets()
    {
        _quietCount = 0;
        Color side = _board.SideToMove;
        for (int i = 0; i < _legalMoves.Count; i++)
        {
            if (IsPicked(i)) continue;
            Move m = _legalMoves[i];
            if (m.IsCapture || m.IsPromotion) continue;

            int score = _history?.GetScore(side, m) ?? 0;
            _scores[i] = score;
            _quietIndices[_quietCount++] = (byte)i;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly int PickBestQuiet()
    {
        int bestIdx = -1;
        int bestScore = int.MinValue;

        for (int j = 0; j < _quietCount; j++)
        {
            int i = _quietIndices[j];
            if (IsPicked(i)) continue;

            int score = _scores[i];
            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly int PickBestBadCapture()
    {
        int bestIdx = -1;
        int bestScore = int.MinValue;

        for (int j = 0; j < _badCaptureCount; j++)
        {
            int i = _badCaptureIndices[j];
            if (IsPicked(i)) continue;

            int score = _scores[i];
            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    /// <summary>
    /// Computes MVV-LVA score for a capture or promotion move using pre-retrieved pieces.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetMvvLva(Piece movedPiece, Piece capturedPiece, Move move)
    {
        int victimVal = capturedPiece != Piece.None
            ? StaticExchangeEvaluator.GetPieceValue(capturedPiece.Type())
            : 0;

        int attackerVal = movedPiece != Piece.None
            ? StaticExchangeEvaluator.GetPieceValue(movedPiece.Type())
            : 0;

        int score = (victimVal * 100) - attackerVal;

        if (move.IsPromotion)
        {
            score += StaticExchangeEvaluator.GetPieceValue(move.PromotionPieceType) * 100;
        }

        return score;
    }

    /// <summary>
    /// Computes MVV-LVA score for a capture or promotion move.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetMvvLva(in Board board, Move move)
    {
        Piece movedPiece = board.GetPieceAt(move.From);
        Piece capturedPiece = move.Type == MoveType.EnPassant
            ? PieceExtensions.Create(board.SideToMove.Opponent(), PieceType.Pawn)
            : board.GetPieceAt(move.To);

        return GetMvvLva(movedPiece, capturedPiece, move);
    }
}
