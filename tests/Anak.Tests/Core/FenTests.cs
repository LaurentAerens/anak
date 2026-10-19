using Anak.Core;
using Xunit;

namespace Anak.Tests.Core;

public class FenTests
{
    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1")]
    [InlineData("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1")]
    [InlineData("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8")]
    [InlineData("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1")]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w Kq - 5 20")]
    [InlineData("8/8/8/8/8/8/8/4K2k w - - 0 1")]
    public void Fen_RoundTrip_MatchesOriginal(string expectedFen)
    {
        var board = Fen.Parse(expectedFen);
        string actualFen = Fen.ToFen(in board);
        Assert.Equal(expectedFen, actualFen);
    }

    [Fact]
    public void Fen_StartPosition_CorrectInitialState()
    {
        var board = Fen.Parse(Fen.StartPosition);

        Assert.Equal(Color.White, board.SideToMove);
        Assert.Equal(CastlingRights.All, board.Castling);
        Assert.Equal(Square.None, board.EpSquare);
        Assert.Equal(0, board.HalfmoveClock);
        Assert.Equal(1, board.FullmoveNumber);

        // Check key pieces
        Assert.Equal(Piece.WhiteKing, board.GetPieceAt(Square.E1));
        Assert.Equal(Piece.BlackKing, board.GetPieceAt(Square.E8));
        Assert.Equal(Piece.WhiteQueen, board.GetPieceAt(Square.D1));
        Assert.Equal(Piece.BlackQueen, board.GetPieceAt(Square.D8));

        Assert.Equal(Piece.WhiteRook, board.GetPieceAt(Square.A1));
        Assert.Equal(Piece.WhiteRook, board.GetPieceAt(Square.H1));
        Assert.Equal(Piece.BlackRook, board.GetPieceAt(Square.A8));
        Assert.Equal(Piece.BlackRook, board.GetPieceAt(Square.H8));

        for (int file = 0; file < 8; file++)
        {
            Assert.Equal(Piece.WhitePawn, board.GetPieceAt(SquareExtensions.Create(file, 1)));
            Assert.Equal(Piece.BlackPawn, board.GetPieceAt(SquareExtensions.Create(file, 6)));
        }

        // Empty squares
        for (int rank = 2; rank <= 5; rank++)
        {
            for (int file = 0; file < 8; file++)
            {
                Assert.Equal(Piece.None, board.GetPieceAt(SquareExtensions.Create(file, rank)));
            }
        }
    }

    [Fact]
    public void Fen_EnPassant_ParsedCorrectly()
    {
        const string fen = "rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq e6 0 2";
        var board = Fen.Parse(fen);

        Assert.Equal(Square.E6, board.EpSquare);
        Assert.Equal(Color.White, board.SideToMove);
        Assert.Equal(CastlingRights.All, board.Castling);
        Assert.Equal(0, board.HalfmoveClock);
        Assert.Equal(2, board.FullmoveNumber);
    }

    [Fact]
    public void Fen_ToString_MatchesToFen()
    {
        var board = Fen.Parse(Fen.StartPosition);
        Assert.Equal(Fen.StartPosition, board.ToString());
    }
}
