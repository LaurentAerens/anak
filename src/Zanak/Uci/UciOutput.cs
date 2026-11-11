using System.Text;
using Anak.Core;
using Zanak.Search;

namespace Zanak.Uci;

/// <summary>
/// High-frequency UCI info stream formatter and standard protocol output emitter.
/// </summary>
public static class UciOutput
{
    public static string FormatInfo(in ZapSearchStats stats)
    {
        var sb = new StringBuilder(128);
        sb.Append("info depth ").Append(stats.Depth);

        // Score formatting
        int score = stats.ScoreCp;
        if (Math.Abs(score) >= 29000)
        {
            int mateIn = score > 0 ? (30000 - score + 1) / 2 : (-30000 - score) / 2;
            sb.Append(" score mate ").Append(mateIn);
        }
        else
        {
            sb.Append(" score cp ").Append(score);
        }

        sb.Append(" time ").Append(stats.ElapsedMs);
        sb.Append(" nodes ").Append(stats.Nodes);
        sb.Append(" nps ").Append(stats.Nps);

        if (stats.Pv is { Length: > 0 })
        {
            sb.Append(" pv");
            for (int i = 0; i < stats.Pv.Length; i++)
            {
                if (!stats.Pv[i].IsNull)
                {
                    sb.Append(' ').Append(stats.Pv[i].ToUci());
                }
            }
        }

        return sb.ToString();
    }

    public static string FormatBestMove(Move move)
    {
        return $"bestmove {move.ToUci()}";
    }
}
