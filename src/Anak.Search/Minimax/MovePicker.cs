using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

[InlineArray(256)]
public struct ScoreBuffer
{
    private int _element0;
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

    private MoveList _legalMoves;
    private ScoreBuffer _scores;
    private MovePickerStage _stage;
    private bool _capturesScored;
    private bool _quietsScored;

    // 256-bit bitmask tracking picked moves
    private ulong _picked0;
    private ulong _picked1;
    private ulong _picked2;
    private ulong _picked3;

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

        _legalMoves = new MoveList();
        MoveGenerator.GenerateLegalMoves(in board, ref _legalMoves);

        _scores = default;
        _stage = MovePickerStage.TtMove;
        _capturesScored = false;
        _quietsScored = false;
        _picked0 = 0;
        _picked1 = 0;
        _picked2 = 0;
        _picked3 = 0;
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

    public readonly int MoveCount => _legalMoves.Count;
    public readonly MovePickerStage Stage => _stage;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool IsPicked(int index)
    {
        int word = index >> 6;
        ulong bit = 1UL << (index & 63);
        return word switch
        {
            0 => (_picked0 & bit) != 0,
            1 => (_picked1 & bit) != 0,
            2 => (_picked2 & bit) != 0,
            3 => (_picked3 & bit) != 0,
            _ => true
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkPicked(int index)
    {
        int word = index >> 6;
        ulong bit = 1UL << (index & 63);
        switch (word)
        {
            case 0: _picked0 |= bit; break;
            case 1: _picked1 |= bit; break;
            case 2: _picked2 |= bit; break;
            case 3: _picked3 |= bit; break;
        }
    }

    /// <summary>
    /// Gets the next move in the staged move ordering.
    /// Returns true if a move was picked, false if no more moves.
    /// </summary>
    public bool Next(out Move move)
    {
        while (_stage != MovePickerStage.Done)
        {
            switch (_stage)
            {
                case MovePickerStage.TtMove:
                    _stage = MovePickerStage.GoodCaptures;
                    if (!_ttMove.IsNull)
                    {
                        int ttIdx = FindMove(_ttMove);
                        if (ttIdx >= 0)
                        {
                            MarkPicked(ttIdx);
                            move = _ttMove;
                            return true;
                        }
                    }
                    break;

                case MovePickerStage.GoodCaptures:
                    if (!_capturesScored)
                    {
                        ScoreCaptures();
                        _capturesScored = true;
                    }

                    int bestCapIdx = PickBestGoodCapture();
                    if (bestCapIdx >= 0)
                    {
                        MarkPicked(bestCapIdx);
                        move = _legalMoves[bestCapIdx];
                        return true;
                    }

                    _stage = MovePickerStage.Killer0;
                    break;

                case MovePickerStage.Killer0:
                    _stage = MovePickerStage.Killer1;
                    if (!_killer0.IsNull && !_killer0.IsCapture && !_killer0.IsPromotion && _killer0 != _ttMove)
                    {
                        int k0Idx = FindMove(_killer0);
                        if (k0Idx >= 0 && !IsPicked(k0Idx))
                        {
                            MarkPicked(k0Idx);
                            move = _killer0;
                            return true;
                        }
                    }
                    break;

                case MovePickerStage.Killer1:
                    _stage = MovePickerStage.CounterMove;
                    if (!_killer1.IsNull && !_killer1.IsCapture && !_killer1.IsPromotion && _killer1 != _ttMove && _killer1 != _killer0)
                    {
                        int k1Idx = FindMove(_killer1);
                        if (k1Idx >= 0 && !IsPicked(k1Idx))
                        {
                            MarkPicked(k1Idx);
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
                        int cmIdx = FindMove(_counterMove);
                        if (cmIdx >= 0 && !IsPicked(cmIdx))
                        {
                            MarkPicked(cmIdx);
                            move = _counterMove;
                            return true;
                        }
                    }
                    break;

                case MovePickerStage.ContinuationMoves:
                    int bestContIdx = PickBestContinuationMove();
                    if (bestContIdx >= 0)
                    {
                        MarkPicked(bestContIdx);
                        move = _legalMoves[bestContIdx];
                        return true;
                    }

                    _stage = MovePickerStage.QuietMoves;
                    break;

                case MovePickerStage.QuietMoves:
                    if (!_quietsScored)
                    {
                        ScoreQuiets();
                        _quietsScored = true;
                    }

                    int bestQuietIdx = PickBestQuiet();
                    if (bestQuietIdx >= 0)
                    {
                        MarkPicked(bestQuietIdx);
                        move = _legalMoves[bestQuietIdx];
                        return true;
                    }

                    _stage = MovePickerStage.BadCaptures;
                    break;

                case MovePickerStage.BadCaptures:
                    int bestBadCapIdx = PickBestBadCapture();
                    if (bestBadCapIdx >= 0)
                    {
                        MarkPicked(bestBadCapIdx);
                        move = _legalMoves[bestBadCapIdx];
                        return true;
                    }

                    _stage = MovePickerStage.Done;
                    break;
            }
        }

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

    private void ScoreCaptures()
    {
        for (int i = 0; i < _legalMoves.Count; i++)
        {
            Move m = _legalMoves[i];
            if (!m.IsCapture && !m.IsPromotion) continue;

            int see = StaticExchangeEvaluator.Evaluate(in _board, m);
            if (see >= 0)
            {
                Piece moved = _board.GetPieceAt(m.From);
                Piece captured = m.Type == MoveType.EnPassant
                    ? PieceExtensions.Create(_board.SideToMove.Opponent(), PieceType.Pawn)
                    : _board.GetPieceAt(m.To);

                int score = GetMvvLva(in _board, m);
                if (_captureHistory != null)
                {
                    score += _captureHistory.GetScore(moved, m.To, captured);
                }
                _scores[i] = score;
            }
            else
            {
                // Bad capture: negative score offset
                _scores[i] = -1_000_000 + see;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly int PickBestGoodCapture()
    {
        int bestIdx = -1;
        int bestScore = -1;

        for (int i = 0; i < _legalMoves.Count; i++)
        {
            if (IsPicked(i)) continue;
            Move m = _legalMoves[i];
            if (!m.IsCapture && !m.IsPromotion) continue;

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
    private readonly int PickBestContinuationMove()
    {
        if (_continuationHistory == null) return -1;

        int bestIdx = -1;
        int bestScore = 0; // Only positive continuation scores

        for (int i = 0; i < _legalMoves.Count; i++)
        {
            if (IsPicked(i)) continue;
            Move m = _legalMoves[i];
            if (m.IsCapture || m.IsPromotion) continue;

            Piece moved = _board.GetPieceAt(m.From);
            int contScore = _continuationHistory.GetScore(_prev1Piece, _prev1To, _prev2Piece, _prev2To, moved, m.To);
            if (contScore > bestScore)
            {
                bestScore = contScore;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    private void ScoreQuiets()
    {
        Color side = _board.SideToMove;
        for (int i = 0; i < _legalMoves.Count; i++)
        {
            if (IsPicked(i)) continue;
            Move m = _legalMoves[i];
            if (m.IsCapture || m.IsPromotion) continue;

            int score = _history?.GetScore(side, m) ?? 0;
            _scores[i] = score;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly int PickBestQuiet()
    {
        int bestIdx = -1;
        int bestScore = int.MinValue;

        for (int i = 0; i < _legalMoves.Count; i++)
        {
            if (IsPicked(i)) continue;
            Move m = _legalMoves[i];
            if (m.IsCapture || m.IsPromotion) continue;

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

        for (int i = 0; i < _legalMoves.Count; i++)
        {
            if (IsPicked(i)) continue;
            Move m = _legalMoves[i];
            if (!m.IsCapture && !m.IsPromotion) continue;

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
    /// Computes MVV-LVA score for a capture or promotion move.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetMvvLva(in Board board, Move move)
    {
        Square from = move.From;
        Square to = move.To;
        Piece movedPiece = board.GetPieceAt(from);
        Piece capturedPiece = move.Type == MoveType.EnPassant
            ? PieceExtensions.Create(board.SideToMove.Opponent(), PieceType.Pawn)
            : board.GetPieceAt(to);

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
}
