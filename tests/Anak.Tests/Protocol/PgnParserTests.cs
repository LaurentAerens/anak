using Anak.Core;
using Anak.Protocol.Analyze;
using Xunit;

namespace Anak.Tests.Protocol;

public class PgnParserTests
{
    [Fact]
    public void ParseSanMove_StandardOpening_ParsesCorrectly()
    {
        Board board = Fen.Parse(Fen.StartPosition);

        Move e4 = PgnParser.ParseSanMove(ref board, "e4");
        Assert.False(e4.IsNull);
        Assert.Equal(Square.E2, e4.From);
        Assert.Equal(Square.E4, e4.To);
        board.MakeMove(e4, out _);

        Move e5 = PgnParser.ParseSanMove(ref board, "e5");
        Assert.False(e5.IsNull);
        Assert.Equal(Square.E7, e5.From);
        Assert.Equal(Square.E5, e5.To);
        board.MakeMove(e5, out _);

        Move nf3 = PgnParser.ParseSanMove(ref board, "Nf3");
        Assert.False(nf3.IsNull);
        Assert.Equal(Square.G1, nf3.From);
        Assert.Equal(Square.F3, nf3.To);
        board.MakeMove(nf3, out _);

        Move nc6 = PgnParser.ParseSanMove(ref board, "Nc6");
        Assert.False(nc6.IsNull);
        Assert.Equal(Square.B8, nc6.From);
        Assert.Equal(Square.C6, nc6.To);
        board.MakeMove(nc6, out _);
    }

    [Fact]
    public void ParseSanMove_Castling_KingsideAndQueenside()
    {
        // Position where White can castle kingside and Black can castle queenside
        // FEN: r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1
        Board board = Fen.Parse("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");

        Move castleWhite = PgnParser.ParseSanMove(ref board, "O-O");
        Assert.False(castleWhite.IsNull);
        Assert.Equal(MoveType.KingCastle, castleWhite.Type);
        board.MakeMove(castleWhite, out _);

        Move castleBlack = PgnParser.ParseSanMove(ref board, "O-O-O");
        Assert.False(castleBlack.IsNull);
        Assert.Equal(MoveType.QueenCastle, castleBlack.Type);
    }

    [Fact]
    public void ParseSanMove_PawnPromotionAndDisambiguation()
    {
        // Position with promotion and knight disambiguation
        // White pawns on e7, d7; Black knight on b8, d8
        Board board = Fen.Parse("1n1n4/3PP3/8/8/8/8/8/4K2k w - - 0 1");

        Move promoQ = PgnParser.ParseSanMove(ref board, "e8=Q");
        Assert.False(promoQ.IsNull);
        Assert.Equal(PieceType.Queen, promoQ.PromotionPieceType);

        Move promoN = PgnParser.ParseSanMove(ref board, "d8=N+");
        Assert.False(promoN.IsNull);
        Assert.Equal(PieceType.Knight, promoN.PromotionPieceType);
    }

    [Fact]
    public void ParseSanMove_DisambiguationByFileAndRank()
    {
        // Two White rooks on a1 and f1, both can move to d1
        Board board = Fen.Parse("4k3/8/8/8/8/8/8/R4R1K w - - 0 1");

        Move rad1 = PgnParser.ParseSanMove(ref board, "Rad1");
        Assert.False(rad1.IsNull);
        Assert.Equal(Square.A1, rad1.From);
        Assert.Equal(Square.D1, rad1.To);

        Move rfd1 = PgnParser.ParseSanMove(ref board, "Rfd1");
        Assert.False(rfd1.IsNull);
        Assert.Equal(Square.F1, rfd1.From);
        Assert.Equal(Square.D1, rfd1.To);
    }

    [Fact]
    public void ParseText_CompletePgnGame_ExtractsTagsAndMoves()
    {
        string pgn = """
            [Event "FIDE World Championship"]
            [Site "Dubai UAE"]
            [Date "2021.11.26"]
            [Round "1"]
            [White "Nepomniachtchi, Ian"]
            [Black "Carlsen, Magnus"]
            [Result "1/2-1/2"]

            1. e4 e5 2. Nf3 Nc6 3. Bc4 Nf6 4. Ng5 d5 5. exd5 Na5 6. d3 Nxc4 7. dxc4 h6 8. Nf3 e4 1/2-1/2
            """;

        List<PgnGame> games = PgnParser.ParseText(pgn);
        Assert.Single(games);

        PgnGame game = games[0];
        Assert.Equal("Nepomniachtchi, Ian", game.White);
        Assert.Equal("Carlsen, Magnus", game.Black);
        Assert.Equal("1/2-1/2", game.Result);
        Assert.Equal(16, game.Moves.Count);

        string finalFen = Fen.ToFen(game.FinalBoard);
        Assert.Contains("r1bqkb1r/ppp2pp1/5n1p/3P4/2P1p3/5N2/PPP2PPP/RNBQK2R w KQkq - 0 9", finalFen);
    }

    [Fact]
    public void ParseText_WithCommentsAndRav_IgnoresExtraneousText()
    {
        string pgn = """
            [Event "Test Comments"]
            [Result "*"]

            1. e4 {Best by test} 1... e5 (1... c5 2. Nf3) 2. Nf3 {+0.25/12} 2... Nc6 *
            """;

        List<PgnGame> games = PgnParser.ParseText(pgn);
        Assert.Single(games);

        PgnGame game = games[0];
        Assert.Equal(4, game.Moves.Count);
        Assert.Equal("*", game.Result);
    }
}
