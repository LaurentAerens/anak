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
    public void FormatUci_VeryGoodPosition_ReportsCentipawnsNotMateInOne()
    {
        // Winning position (e.g. +15 pawns), RootScore > 0.98f, but no proven checkmate
        SearchStatistics stats = new()
        {
            ScoreCp = 1500,
            RootScore = 0.995f,
            MateIn = 0
        };

        string uciScore = SearchScoreFormat.FormatUci(stats);
        Assert.Equal("cp 1500", uciScore);
        Assert.DoesNotContain("mate", uciScore);

        // Crushing advantage for Black
        stats.ScoreCp = -1500;
        stats.RootScore = -0.995f;
        uciScore = SearchScoreFormat.FormatUci(stats);
        Assert.Equal("cp -1500", uciScore);
        Assert.DoesNotContain("mate", uciScore);
    }

    [Fact]
    public void FormatUci_SaturatedProbabilityWithoutCentipawns_ReportsHighEvalNotMateInOne()
    {
        // Saturated win probability (e.g. MCTS root score) without mate-bound score
        SearchStatistics stats = new()
        {
            ScoreCp = 0,
            RootScore = 0.999f,
            MateIn = 0
        };

        string uciScore = SearchScoreFormat.FormatUci(stats);
        Assert.StartsWith("cp ", uciScore);
        Assert.NotEqual("mate 1", uciScore);
        Assert.True(int.Parse(uciScore.Substring(3)) > 1000);
    }

    [Fact]
    public void FormatUci_ActualMate_ReportsAccurateMateDistance()
    {
        SearchStatistics stats = new()
        {
            MateIn = 1
        };

        string uciScore = SearchScoreFormat.FormatUci(stats);
        Assert.Equal("mate 1", uciScore);

        stats.MateIn = -4;
        uciScore = SearchScoreFormat.FormatUci(stats);
        Assert.Equal("mate -4", uciScore);
    }

    [Fact]
    public void FormatHuman_SaturatedForcedWinWithoutMate_Uses255Sentinel()
    {
        SearchStatistics stats = new()
        {
            MateIn = 0,
            RootScore = 0.99f
        };

        string formatted = SearchScoreFormat.FormatHuman(stats);
        Assert.Equal("+M255", formatted);

        stats.RootScore = -0.99f;
        formatted = SearchScoreFormat.FormatHuman(stats);
        Assert.Equal("-M255", formatted);
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
