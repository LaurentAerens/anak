using System.Globalization;

namespace Anak.Search;

/// <summary>
/// Converts <see cref="SearchStatistics"/> into human-readable and UCI score strings.
/// </summary>
public static class SearchScoreFormat
{
    /// <summary>
    /// Display-only sentinel: forced win / mate is coming but exact distance is not known yet
    /// (e.g. NN win probability saturated without a mate-bound search score).
    /// </summary>
    public const int UnknownMateDistance = 256;

    /// <summary>
    /// Centipawns for side to move, using <see cref="SearchStatistics.ScoreCp"/> or inverting <see cref="SearchStatistics.RootScore"/> via atanh.
    /// </summary>
    public static int ResolveCentipawns(in SearchStatistics stats)
    {
        if (stats.ScoreCp != 0)
        {
            return stats.ScoreCp;
        }

        if (stats.RootScore != 0.0f)
        {
            float clamped = Math.Clamp(stats.RootScore, -0.999f, 0.999f);
            return (int)(MathF.Atanh(clamped) * 400.0f);
        }

        return 0;
    }

    /// <summary>
    /// True when root win probability is saturated but the search did not report a mate-bound score.
    /// </summary>
    public static bool IsForcedWinWithoutMate(in SearchStatistics stats)
        => stats.MateIn == 0 && MathF.Abs(stats.RootScore) > 0.98f;

    /// <summary>
    /// Mate distance for display and UCI: uses engine mate score, but never below half-moves implied by the PV
    /// (avoids shallow "M1" labels when the principal variation is much longer).
    /// </summary>
    public static int GetDisplayMateIn(in SearchStatistics stats)
    {
        if (stats.MateIn == 0)
        {
            return 0;
        }

        if (stats.MateIn == UnknownMateDistance)
        {
            return UnknownMateDistance;
        }

        int mate = stats.MateIn;
        if (stats.Pv is { Count: > 0 })
        {
            if (mate > 0)
            {
                int mateFromPv = (stats.Pv.Count + 1) / 2;
                if (mateFromPv > mate)
                {
                    mate = mateFromPv;
                }
            }
            else if (mate < 0)
            {
                int mateFromPv = -((stats.Pv.Count + 1) / 2);
                if (mateFromPv < mate)
                {
                    mate = mateFromPv;
                }
            }
        }

        return mate;
    }

    /// <summary>
    /// Human-readable score (analyze CLI, logs): pawns, mate distance, or unknown forced mate (+M256).
    /// </summary>
    public static string FormatHuman(in SearchStatistics stats)
    {
        if (IsForcedWinWithoutMate(stats))
        {
            return stats.RootScore > 0 ? $"+M{UnknownMateDistance}" : $"-M{UnknownMateDistance}";
        }

        int displayMate = GetDisplayMateIn(stats);
        if (displayMate != 0)
        {
            return displayMate > 0 ? $"+M{displayMate}" : $"-M{Math.Abs(displayMate)}";
        }

        int cp = ResolveCentipawns(stats);
        float pawns = cp / 100.0f;
        return $"{(pawns >= 0 ? "+" : "")}{pawns.ToString("F2", CultureInfo.InvariantCulture)} ({cp:+0;-0;0} cp)";
    }

    /// <summary>
    /// UCI "score cp N" or "score mate N" fragment (without the "score " prefix).
    /// </summary>
    public static string FormatUci(in SearchStatistics stats)
    {
        if (IsForcedWinWithoutMate(stats))
        {
            return $"cp {ResolveCentipawns(stats)}";
        }

        int displayMate = GetDisplayMateIn(stats);
        if (displayMate != 0)
        {
            return $"mate {displayMate}";
        }

        return $"cp {ResolveCentipawns(stats)}";
    }
}
