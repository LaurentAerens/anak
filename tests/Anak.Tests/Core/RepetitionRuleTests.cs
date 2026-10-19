using Anak.Core;
using Xunit;

namespace Anak.Tests.Core;

public class RepetitionRuleTests
{
    [Fact]
    public void ThreefoldRepetition_StandardKnightsDance_Detected()
    {
        var board = Fen.Parse(Fen.StartPosition);
        Assert.False(board.IsThreefoldRepetition());

        // 1. Nf3 Nf6 (ply 1, 2)
        board.MakeMove(new Move(Square.G1, Square.F3), out _);
        board.MakeMove(new Move(Square.G8, Square.F6), out _);
        Assert.False(board.IsThreefoldRepetition());

        // 2. Ng1 Ng8 (ply 3, 4) - Position occurs 2nd time
        board.MakeMove(new Move(Square.F3, Square.G1), out _);
        board.MakeMove(new Move(Square.F6, Square.G8), out _);
        Assert.False(board.IsThreefoldRepetition());

        // 3. Nf3 Nf6 (ply 5, 6)
        board.MakeMove(new Move(Square.G1, Square.F3), out _);
        board.MakeMove(new Move(Square.G8, Square.F6), out _);
        Assert.False(board.IsThreefoldRepetition());

        // 4. Ng1 Ng8 (ply 7, 8) - Position occurs 3rd time!
        board.MakeMove(new Move(Square.F3, Square.G1), out _);
        board.MakeMove(new Move(Square.F6, Square.G8), out _);

        Assert.True(board.IsThreefoldRepetition(), "Threefold repetition must be detected on 3rd occurrence!");
    }

    [Fact]
    public void ThreefoldRepetition_TransposedMoveOrders_Detected()
    {
        var board = Fen.Parse(Fen.StartPosition);

        // First occurrence is Startpos (count = 1)

        // Path A: 1. Nf3 Nf6 2. Ng1 Ng8 (count = 2)
        board.MakeMove(new Move(Square.G1, Square.F3), out _);
        board.MakeMove(new Move(Square.G8, Square.F6), out _);
        board.MakeMove(new Move(Square.F3, Square.G1), out _);
        board.MakeMove(new Move(Square.F6, Square.G8), out _);
        Assert.False(board.IsThreefoldRepetition());

        // Path B (transposed via Queenside knights): 3. Nc3 Nc6 4. Nb1 Nb8 (count = 3!)
        board.MakeMove(new Move(Square.B1, Square.C3), out _);
        board.MakeMove(new Move(Square.B8, Square.C6), out _);
        board.MakeMove(new Move(Square.C3, Square.B1), out _);
        board.MakeMove(new Move(Square.C6, Square.B8), out _);

        Assert.True(board.IsThreefoldRepetition(), "Threefold repetition reached via transposed moves must be detected!");
    }

    [Fact]
    public void FiftyMoveRule_TriggersAt100Halfmoves()
    {
        // Set up a board with 99 halfmoves
        var board = Fen.Parse("8/8/4k3/8/8/4K3/8/8 w - - 99 50");
        Assert.Equal(99, board.HalfmoveClock);
        Assert.False(board.IsFiftyMoveRule());

        // Make 100th halfmove (quiet king move)
        board.MakeMove(new Move(Square.E3, Square.D3), out _);

        Assert.Equal(100, board.HalfmoveClock);
        Assert.True(board.IsFiftyMoveRule(), "Fifty-move rule must trigger when HalfmoveClock reaches 100 plies!");
    }

    [Fact]
    public void FiftyMoveRule_ResetsOnPawnPush()
    {
        var board = Fen.Parse("8/8/4k3/8/4P3/8/8/4K3 w - - 99 50");
        Assert.Equal(99, board.HalfmoveClock);

        // Make pawn push move
        board.MakeMove(new Move(Square.E4, Square.E5), out _);

        Assert.Equal(0, board.HalfmoveClock);
        Assert.False(board.IsFiftyMoveRule());
    }

    [Fact]
    public void FiftyMoveRule_ResetsOnCapture()
    {
        var board = Fen.Parse("8/8/4k3/8/4n3/8/8/4K1R1 w - - 99 50");
        Assert.Equal(99, board.HalfmoveClock);

        // Make capture move: Rg1xe4
        board.MakeMove(new Move(Square.G1, Square.E4, MoveType.Capture), out _);

        Assert.Equal(0, board.HalfmoveClock);
        Assert.False(board.IsFiftyMoveRule());
    }

    [Fact]
    public void Stalemate_Detection()
    {
        // Classic stalemate: Black King on a8, White Queen on c7, White King on e1
        // FEN: k7/2Q5/8/8/8/8/8/4K3 b - - 0 1
        var board = Fen.Parse("k7/2Q5/8/8/8/8/8/4K3 b - - 0 1");
        Assert.False(board.IsInCheck());

        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);
        Assert.Equal(0, moves.Count);

        bool isOver = board.IsGameOver(out GameResult result);
        Assert.True(isOver);
        Assert.Equal(GameResult.Stalemate, result);
    }

    [Fact]
    public void Checkmate_Detection_FoolsMate()
    {
        // Fool's mate: 1. f3 e5 2. g4 Qh4#
        // FEN: rnb1kbnr/pppp1ppp/4p3/8/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 1 3
        var board = Fen.Parse("rnb1kbnr/pppp1ppp/4p3/8/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 1 3");
        Assert.True(board.IsInCheck());

        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);
        Assert.Equal(0, moves.Count);

        bool isOver = board.IsGameOver(out GameResult result);
        Assert.True(isOver);
        Assert.Equal(GameResult.Checkmate, result);
    }

    [Fact]
    public void Checkmate_Detection_ScholarsMate()
    {
        // Scholar's mate: Qxf7#
        // FEN: r1bqkb1r/pppp1Qpp/2n2n2/4p3/2B1P3/8/PPPP1PPP/RNB1K1NR b KQkq - 0 4
        var board = Fen.Parse("r1bqkb1r/pppp1Qpp/2n2n2/4p3/2B1P3/8/PPPP1PPP/RNB1K1NR b KQkq - 0 4");
        Assert.True(board.IsInCheck());

        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);
        Assert.Equal(0, moves.Count);

        bool isOver = board.IsGameOver(out GameResult result);
        Assert.True(isOver);
        Assert.Equal(GameResult.Checkmate, result);
    }

    [Theory]
    [InlineData("8/8/4k3/8/8/4K3/8/8 w - - 0 1", true)]                   // K vs K
    [InlineData("8/8/4k3/8/8/4KN2/8/8 w - - 0 1", true)]                  // K+N vs K
    [InlineData("8/8/4k3/8/8/4KB2/8/8 w - - 0 1", true)]                  // K+B vs K
    [InlineData("8/8/4k3/8/8/2B5/8/4K1b1 w - - 0 1", true)]               // K+B vs K+B (same color: c3 and g1 both dark)
    [InlineData("8/8/4k3/8/4P3/8/8/4K3 w - - 0 1", false)]                 // K+P vs K (pawn can promote)
    [InlineData("8/8/4k3/8/8/8/8/R3K3 w - - 0 1", false)]                 // K+R vs K (can checkmate)
    [InlineData("8/8/4k3/8/8/8/8/Q3K3 w - - 0 1", false)]                 // K+Q vs K (can checkmate)
    public void InsufficientMaterial_Verification(string fen, bool expected)
    {
        var board = Fen.Parse(fen);
        Assert.Equal(expected, board.HasInsufficientMaterial());
    }
}
