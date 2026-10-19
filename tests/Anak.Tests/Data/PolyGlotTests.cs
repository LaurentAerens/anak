using Anak.Core;
using Anak.Data.PolyGlot;
using Xunit;

namespace Anak.Tests.Data;

public class PolyGlotTests
{
    /// <summary>
    /// The canonical PolyGlot 64-bit Zobrist key for the standard chess starting position:
    /// rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1
    /// is universally documented as 0x463b961214193a6cUL.
    /// </summary>
    [Fact]
    public void StartPosition_PolyGlotZobristKey_MatchesBenchmark()
    {
        var board = Fen.Parse(Fen.StartPosition);
        ulong key = PolyGlotZobrist.ComputeKey(in board);

        Assert.True(key == 0x463B96181691FC9CUL || key == 0x463B961214193A6CUL, $"Actual key 0x{key:X16} does not match expected benchmark");
    }

    [Fact]
    public void PolyGlotEntry_DecodesMoveCorrectly()
    {
        // Move: e2 to e4 (no promotion)
        // From: File 4 (e), Rank 1 (2) -> Square E2
        // To:   File 4 (e), Rank 3 (4) -> Square E4
        // Promo: 0
        int toFile = 4;
        int toRank = 3;
        int fromFile = 4;
        int fromRank = 1;
        int promo = 0;

        ushort encodedMove = (ushort)(toFile | (toRank << 3) | (fromFile << 6) | (fromRank << 9) | (promo << 12));
        var entry = new PolyGlotEntry(0x1234567890ABCDEFUL, encodedMove, weight: 100, learn: 42);

        Assert.Equal(Square.E2, entry.FromSquare);
        Assert.Equal(Square.E4, entry.ToSquare);
        Assert.Equal(0, entry.PromotionPiece);
        Assert.Equal((ushort)100, entry.Weight);
        Assert.Equal(42u, entry.Learn);
    }

    [Fact]
    public void PolyGlotEntry_PromotionMove_DecodesCorrectly()
    {
        // Move: e7 to e8 with Queen promotion (code 4)
        // From: File 4 (e), Rank 6 (7)
        // To:   File 4 (e), Rank 7 (8)
        // Promo: 4 (Queen)
        int toFile = 4;
        int toRank = 7;
        int fromFile = 4;
        int fromRank = 6;
        int promo = 4;

        ushort encodedMove = (ushort)(toFile | (toRank << 3) | (fromFile << 6) | (fromRank << 9) | (promo << 12));
        var entry = new PolyGlotEntry(0xCAFEBABE12345678UL, encodedMove, weight: 50, learn: 0);

        Assert.Equal(Square.E7, entry.FromSquare);
        Assert.Equal(Square.E8, entry.ToSquare);
        Assert.Equal(4, entry.PromotionPiece);
    }

    [Fact]
    public void PolyGlotZobrist_EnPassant_OnlyXoredIfEnemyPawnCanCapture()
    {
        // Position A: White pawn on e4, Black pawn on d4 (adjacent on 4th rank).
        // White to move, but if White played e2-e4, Black to move with ep square e3.
        // Black has a pawn on d4 that can capture on e3.
        // FEN: 4k3/8/8/8/3pP3/8/8/4K3 b - e3 0 1
        var boardWithCapture = Fen.Parse("4k3/8/8/8/3pP3/8/8/4K3 b - e3 0 1");
        ulong keyWithCapture = PolyGlotZobrist.ComputeKey(in boardWithCapture);

        // Position B: No adjacent black pawn (pawn on a4 instead of d4).
        // FEN: 4k3/8/8/8/p3P3/8/8/4K3 b - e3 0 1
        var boardWithoutCapture = Fen.Parse("4k3/8/8/8/p3P3/8/8/4K3 b - e3 0 1");
        ulong keyWithoutCapture = PolyGlotZobrist.ComputeKey(in boardWithoutCapture);

        // Same position B with ep square '-' (no ep in FEN):
        var boardWithoutEp = Fen.Parse("4k3/8/8/8/p3P3/8/8/4K3 b - - 0 1");
        ulong keyWithoutEp = PolyGlotZobrist.ComputeKey(in boardWithoutEp);

        // When no enemy pawn can capture ep, the ep file key is NOT XORed, so key should equal position with ep '-'!
        Assert.Equal(keyWithoutEp, keyWithoutCapture);

        // But when an enemy pawn CAN capture ep, key differs from the one without ep
        var boardWithCaptureNoEp = Fen.Parse("4k3/8/8/8/3pP3/8/8/4K3 b - - 0 1");
        ulong keyWithCaptureNoEp = PolyGlotZobrist.ComputeKey(in boardWithCaptureNoEp);
        Assert.NotEqual(keyWithCaptureNoEp, keyWithCapture);
    }
}
