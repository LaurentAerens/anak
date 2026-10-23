using Anak.Core;
using Xunit;

namespace Anak.Tests.Core;

public class MoveGenTests
{
    [Fact]
    public void EnPassant_HorizontalDiscoveredCheck_IsIllegal()
    {
        // Classic horizontal en passant pin:
        // White King on h5, White pawn on d5, Black pawn on e5, Black rook on a5.
        // If White plays d5xe6 e.p., both pawns leave rank 5, exposing White King to the Black rook on a5.
        // FEN: 7k/8/8/r2Pp2K/8/8/8/8 w - e6 0 1
        var board = Fen.Parse("7k/8/8/r2Pp2K/8/8/8/8 w - e6 0 1");
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        // Verify d5xe6 e.p. is NOT among legal moves
        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            bool isEpCapture = m.From == Square.D5 && m.To == Square.E6;
            Assert.False(isEpCapture, "En passant capture exposing king to horizontal check must be illegal!");
        }
    }

    [Fact]
    public void EnPassant_VerticalDiscoveredCheck_IsIllegal()
    {
        // Black King on e8, Black pawn on e4, White rook on e1.
        // White plays d2-d4 -> ep square is d3.
        // If Black plays e4xd3 e.p., Black pawn leaves the e-file, exposing Black King to White Rook on e1.
        // FEN: 4k3/8/8/8/3Pp3/8/8/4R3 b - d3 0 1
        var board = Fen.Parse("4k3/8/8/8/3Pp3/8/8/4R3 b - d3 0 1");
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            bool isEpCapture = m.From == Square.E4 && m.To == Square.D3;
            Assert.False(isEpCapture, "En passant capture exposing king to vertical check must be illegal!");
        }
    }

    [Fact]
    public void EnPassant_Unpinned_IsLegal()
    {
        // Black pawn on e4, White pawn on d4, ep square d3.
        // No rooks or pins on rank or file. e4xd3 should be legal.
        var board = Fen.Parse("8/8/8/8/3Pp3/8/8/4K2k b - d3 0 1");
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        bool foundEp = false;
        for (int i = 0; i < moves.Count; i++)
        {
            if (moves[i].From == Square.E4 && moves[i].To == Square.D3)
            {
                foundEp = true;
                break;
            }
        }
        Assert.True(foundEp, "Unpinned en passant capture must be legal!");
    }

    [Fact]
    public void Castling_ThroughCheck_IsIllegal()
    {
        // Black bishop on c4 attacks f1 (transit square for White kingside castle).
        // FEN: r3k2r/8/8/8/2b5/8/8/R3K2R w KQkq - 0 1
        var board = Fen.Parse("r3k2r/8/8/8/2b5/8/8/R3K2R w KQkq - 0 1");
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        bool foundKingside = false;
        bool foundQueenside = false;
        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            if (m.From == Square.E1 && m.To == Square.G1) foundKingside = true;
            if (m.From == Square.E1 && m.To == Square.C1) foundQueenside = true;
        }

        Assert.False(foundKingside, "Castling through attacked square f1 must be illegal!");
        Assert.True(foundQueenside, "Queenside castling when path is unattacked should be legal!");
    }

    [Fact]
    public void Castling_IntoCheck_IsIllegal()
    {
        // Black rook on g8 attacks g1 (destination square for White kingside castle).
        // FEN: 6r1/8/8/8/8/8/8/R3K2R w KQ - 0 1
        var board = Fen.Parse("6r1/8/8/8/8/8/8/R3K2R w KQ - 0 1");
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        bool foundKingside = false;
        for (int i = 0; i < moves.Count; i++)
        {
            if (moves[i].From == Square.E1 && moves[i].To == Square.G1) foundKingside = true;
        }

        Assert.False(foundKingside, "Castling into attacked square g1 must be illegal!");
    }

    [Fact]
    public void Castling_QueensideB1Attacked_IsLegal()
    {
        // Black rook on b8 attacks b1. b1 is NOT a transit square for King (e1 -> c1, passing d1).
        // FEN: 1r6/8/8/8/8/8/8/R3K2R w KQ - 0 1
        var board = Fen.Parse("1r6/8/8/8/8/8/8/R3K2R w KQ - 0 1");
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        bool foundQueenside = false;
        for (int i = 0; i < moves.Count; i++)
        {
            if (moves[i].From == Square.E1 && moves[i].To == Square.C1) foundQueenside = true;
        }

        Assert.True(foundQueenside, "Queenside castling with b1 attacked is legal according to FIDE rules!");
    }

    [Fact]
    public void Castling_WhileInCheck_IsIllegal()
    {
        // White King on e1 in check from Black Rook on e8.
        // FEN: 4r3/8/8/8/8/8/8/R3K2R w KQ - 0 1
        var board = Fen.Parse("4r3/8/8/8/8/8/8/R3K2R w KQ - 0 1");
        Assert.True(board.IsInCheck());

        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            Assert.False(m.From == Square.E1 && m.To == Square.G1, "Cannot castle kingside while in check!");
            Assert.False(m.From == Square.E1 && m.To == Square.C1, "Cannot castle queenside while in check!");
        }
    }

    [Fact]
    public void PinnedPiece_FilePin_BishopHasNoMoves()
    {
        // White King on e1, White Bishop on e2, Black Rook on e8.
        // Bishop on e2 is absolutely pinned along the e-file.
        // FEN: 4r3/8/8/8/8/8/4B3/4K3 w - - 0 1
        var board = Fen.Parse("4r3/8/8/8/8/8/4B3/4K3 w - - 0 1");
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        for (int i = 0; i < moves.Count; i++)
        {
            Assert.NotEqual(Square.E2, moves[i].From);
        }
    }

    [Fact]
    public void PinnedPiece_FilePin_RookCanOnlyMoveAlongPinRay()
    {
        // White King on e1, White Rook on e4, Black Rook on e8.
        // White Rook on e4 can move along e-file (capturing e8 or moving between), but NOT sideways.
        // FEN: 4r3/8/8/8/4R3/8/8/4K3 w - - 0 1
        var board = Fen.Parse("4r3/8/8/8/4R3/8/8/4K3 w - - 0 1");
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            if (m.From == Square.E4)
            {
                // Must remain on the e-file!
                Assert.Equal(SquareExtensions.File(Square.E4), SquareExtensions.File(m.To));
            }
        }
    }

    [Fact]
    public void DoubleCheck_KingMustMove()
    {
        // White King on e1 in double check: Black Rook on e8 + Black Knight on d3.
        // White has pieces that could otherwise move, but king must move!
        // FEN: 4r3/8/8/8/8/3n4/8/3QK3 w - - 0 1
        var board = Fen.Parse("4r3/8/8/8/8/3n4/8/3QK3 w - - 0 1");
        Assert.True(board.IsInCheck());

        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        Assert.True(moves.Count > 0);
        for (int i = 0; i < moves.Count; i++)
        {
            // Only King (e1) moves are allowed
            Assert.Equal(Square.E1, moves[i].From);
        }
    }

    [Fact]
    public void Promotion_ProducesAllFourPieces()
    {
        // White pawn on e7 pushing to e8.
        // FEN: 7k/4P3/8/8/8/8/8/4K3 w - - 0 1
        var board = Fen.Parse("7k/4P3/8/8/8/8/8/4K3 w - - 0 1");
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        int promoCount = 0;
        bool hasQueen = false, hasRook = false, hasBishop = false, hasKnight = false;

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            if (m.From == Square.E7 && m.To == Square.E8)
            {
                promoCount++;
                if (m.PromotionPieceType == PieceType.Queen) hasQueen = true;
                if (m.PromotionPieceType == PieceType.Rook) hasRook = true;
                if (m.PromotionPieceType == PieceType.Bishop) hasBishop = true;
                if (m.PromotionPieceType == PieceType.Knight) hasKnight = true;
            }
        }

        Assert.Equal(4, promoCount);
        Assert.True(hasQueen, "Must generate Queen promotion");
        Assert.True(hasRook, "Must generate Rook promotion");
        Assert.True(hasBishop, "Must generate Bishop promotion");
        Assert.True(hasKnight, "Must generate Knight promotion");
    }

    [Fact]
    public void IsLegal_AllGeneratedMovesAreLegal_AndIllegalMovesAreRejected()
    {
        string[] testPositions =
        [
            Fen.StartPosition,
            "r1bqk2r/pppp1ppp/2n5/4p3/2B1n3/3P1N2/PPP2PPP/RNBQK2R w KQkq - 0 6",
            "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
            "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
            "7k/8/8/r2Pp2K/8/8/8/8 w - e6 0 1",
            "7k/4P3/8/8/8/8/8/4K3 w - - 0 1"
        ];

        foreach (var fen in testPositions)
        {
            var board = Fen.Parse(fen);
            MoveList moves = default;
            board.GenerateLegalMoves(ref moves);

            Assert.True(moves.Count > 0);
            for (int i = 0; i < moves.Count; i++)
            {
                Assert.True(board.IsLegal(moves[i]), $"Move {moves[i]} should be legal in {fen}");
            }

            // Null move must be illegal
            Assert.False(board.IsLegal(Move.Null));

            // Move from empty square must be illegal
            Assert.False(board.IsLegal(new Move(Square.D4, Square.D5, MoveType.Quiet)));

            // King moving onto enemy occupied square with friendly piece must be illegal
            Assert.False(board.IsLegal(new Move(Square.E1, Square.E1, MoveType.Quiet)));
        }
    }
}
