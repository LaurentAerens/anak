using System;
using Anak.Core;
using Anak.Protocol.Uci;
using Xunit;

namespace Anak.Tests.Data;

public class EvaluatorTests
{
    private static IEvaluator GetEvaluator() => UciEngineAdapter.CreateEvaluator();

    [Fact]
    public void Evaluator_StartPosition_Symmetric()
    {
        var evaluator = GetEvaluator();
        var board = Fen.Parse(Fen.StartPosition);

        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        Value score = evaluator.Evaluate(in board, moves.AsSpan());

        Assert.Equal(0, score.Raw);
    }

    [Fact]
    public void Evaluator_MaterialAdvantage_IsPositiveForSideToMove()
    {
        var evaluator = GetEvaluator();

        // White has an extra Queen: White King on e1, Queen on d1. Black has King on e8.
        // FEN: 4k3/8/8/8/8/8/8/3QK3 w - - 0 1
        var whiteAhead = Fen.Parse("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");
        MoveList wMoves = default;
        whiteAhead.GenerateLegalMoves(ref wMoves);

        Value wScore = evaluator.Evaluate(in whiteAhead, wMoves.AsSpan());
        Assert.True(wScore.Raw > 500, $"Expected large positive score for extra Queen, got {wScore.Raw}");
    }

    [Fact]
    public void Evaluator_ColorSymmetry_MirroredPositionsEqual()
    {
        var evaluator = GetEvaluator();

        // White has extra Queen on d4, King on e1. Black King on e8. White to move.
        // FEN: 4k3/8/8/8/3Q4/8/8/4K3 w - - 0 1
        var whitePos = Fen.Parse("4k3/8/8/8/3Q4/8/8/4K3 w - - 0 1");
        MoveList wMoves = default;
        whitePos.GenerateLegalMoves(ref wMoves);
        Value wScore = evaluator.Evaluate(in whitePos, wMoves.AsSpan());

        // Mirrored position: Black has extra Queen on d5, King on e8. White King on e1. Black to move.
        // FEN: 4k3/8/8/3q4/8/8/8/4K3 b - - 0 1
        var blackPos = Fen.Parse("4k3/8/8/3q4/8/8/8/4K3 b - - 0 1");
        MoveList bMoves = default;
        blackPos.GenerateLegalMoves(ref bMoves);
        Value bScore = evaluator.Evaluate(in blackPos, bMoves.AsSpan());

        // Both players to move with identical material advantage should evaluate symmetrically!
        Assert.Equal(wScore.Raw, bScore.Raw);
    }

    [Fact]
    public void Evaluator_PerspectiveFlipping_OppositeScores()
    {
        var evaluator = GetEvaluator();

        // Position where White has extra Rook:
        // White King on e1, Rook on a1. Black King on e8.
        // When White to move:
        var whiteToMove = Fen.Parse("4k3/8/8/8/8/8/8/R3K3 w - - 0 1");
        MoveList wMoves = default;
        whiteToMove.GenerateLegalMoves(ref wMoves);
        Value wScore = evaluator.Evaluate(in whiteToMove, wMoves.AsSpan());

        // When Black to move in identical material position:
        var blackToMove = Fen.Parse("4k3/8/8/8/8/8/8/R3K3 b - - 0 1");
        MoveList bMoves = default;
        blackToMove.GenerateLegalMoves(ref bMoves);
        Value bScore = evaluator.Evaluate(in blackToMove, bMoves.AsSpan());

        // White is winning, so White to move > 0, Black to move < 0
        Assert.True(wScore.Raw > 0, $"White perspective should be positive, got {wScore.Raw}");
        Assert.True(bScore.Raw < 0, $"Black perspective should be negative, got {bScore.Raw}");
    }

    [Fact]
    public void Evaluator_MaterialOrdering_QueenGreaterThanRookGreaterThanKnight()
    {
        var evaluator = GetEvaluator();

        // Queen vs King
        var posQ = Fen.Parse("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");
        MoveList movesQ = default; posQ.GenerateLegalMoves(ref movesQ);
        Value scoreQ = evaluator.Evaluate(in posQ, movesQ.AsSpan());

        // Rook vs King
        var posR = Fen.Parse("4k3/8/8/8/8/8/8/R3K3 w - - 0 1");
        MoveList movesR = default; posR.GenerateLegalMoves(ref movesR);
        Value scoreR = evaluator.Evaluate(in posR, movesR.AsSpan());

        // Knight vs King
        var posN = Fen.Parse("4k3/8/8/8/8/8/8/1N2K3 w - - 0 1");
        MoveList movesN = default; posN.GenerateLegalMoves(ref movesN);
        Value scoreN = evaluator.Evaluate(in posN, movesN.AsSpan());

        Assert.True(scoreQ.Raw > scoreR.Raw, $"Queen score ({scoreQ.Raw}) should exceed Rook score ({scoreR.Raw})");
        Assert.True(scoreR.Raw > scoreN.Raw, $"Rook score ({scoreR.Raw}) should exceed Knight score ({scoreN.Raw})");
    }
}
