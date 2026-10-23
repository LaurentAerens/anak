using Anak.Core;
using Anak.Protocol.Analyze;
using Anak.Search;
using Xunit;

namespace Anak.Tests.Protocol;

public class AnalyzeTests
{
    [Fact]
    public void FormatScore_Centipawns_FormatsCleanly()
    {
        SearchStatistics stats = new()
        {
            ScoreCp = 45,
            RootScore = 0.1f
        };

        string formatted = AnalyzeRunner.FormatScore(stats);
        Assert.Equal("+0.45 (+45 cp)", formatted);

        stats.ScoreCp = -120;
        formatted = AnalyzeRunner.FormatScore(stats);
        Assert.Equal("-1.20 (-120 cp)", formatted);
    }

    [Fact]
    public void FormatScore_Mate_FormatsCleanly()
    {
        SearchStatistics stats = new()
        {
            MateIn = 3
        };

        string formatted = AnalyzeRunner.FormatScore(stats);
        Assert.Equal("+M3", formatted);

        stats.MateIn = -2;
        formatted = AnalyzeRunner.FormatScore(stats);
        Assert.Equal("-M2", formatted);
    }

    [Fact]
    public void FormatPv_FormatsMoveSequence()
    {
        SearchStatistics stats = new()
        {
            Pv =
            [
                new Move(Square.E2, Square.E4),
                new Move(Square.E7, Square.E5),
                new Move(Square.G1, Square.F3)
            ]
        };

        string pvStr = AnalyzeRunner.FormatPv(stats);
        Assert.Equal("e2e4 e7e5 g1f3", pvStr);
    }

    [Fact]
    public void AnalyzeBoard_ExecutesSearchAndOutputsSummary()
    {
        Board board = Fen.Parse(Fen.StartPosition);
        List<string> lines = [];

        AnalyzeRunner.AnalyzeBoard(in board, depth: 3, threads: 1, hashMb: 16, Fen.StartPosition, lines.Add);

        Assert.Contains(lines, l => l.Contains("Anak Chess Engine — Position Analysis"));
        Assert.Contains(lines, l => l.Contains("Analysis Summary:"));
        Assert.Contains(lines, l => l.Contains("Best Move :"));
        Assert.Contains(lines, l => l.Contains("Evaluation:"));
    }

    [Fact]
    public void AnalyzeFen_InvalidFen_ReportsErrorGracefully()
    {
        List<string> lines = [];
        AnalyzeRunner.AnalyzeFen("invalid fen string", depth: 3, threads: 1, hashMb: 16, lines.Add);

        Assert.Contains(lines, l => l.StartsWith("Error:"));
    }

    [Fact]
    public void AnalyzePgn_MissingFile_ReportsErrorGracefully()
    {
        List<string> lines = [];
        AnalyzeRunner.AnalyzePgn("non_existent_file.pgn", depth: 3, threads: 1, hashMb: 16, lines.Add);

        Assert.Contains(lines, l => l.StartsWith("Error:"));
    }
}
