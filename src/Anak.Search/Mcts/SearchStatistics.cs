using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Diagnostic search statistics reported periodically during search for UCI 'info' commands.
/// </summary>
public struct SearchStatistics
{
    /// <summary>
    /// Nominal search depth reached (most-visited PV depth).
    /// </summary>
    public int Depth { get; set; }

    /// <summary>
    /// Maximum selective depth reached in any branch of the tree.
    /// </summary>
    public int SelDepth { get; set; }

    /// <summary>
    /// Total MCTS iteration count / nodes traversed.
    /// </summary>
    public ulong Nodes { get; set; }

    /// <summary>
    /// Elapsed search time in milliseconds.
    /// </summary>
    public long TimeMs { get; set; }

    /// <summary>
    /// Search speed in Nodes Per Second.
    /// </summary>
    public ulong Nps { get; set; }

    /// <summary>
    /// Current best move from the root position.
    /// </summary>
    public Move BestMove { get; set; }

    /// <summary>
    /// Principal variation sequence of best moves.
    /// </summary>
    public List<Move> Pv { get; set; }

    /// <summary>
    /// Transposition table occupancy in permille (0 to 1000).
    /// </summary>
    public int HashfullPermille { get; set; }

    /// <summary>
    /// Endgame tablebase hits encountered during search.
    /// </summary>
    public ulong TbHits { get; set; }

    /// <summary>
    /// Root evaluation score in Q [-1.0, 1.0].
    /// </summary>
    public float RootScore { get; set; }

    public SearchStatistics()
    {
        Depth = 0;
        SelDepth = 0;
        Nodes = 0;
        TimeMs = 0;
        Nps = 0;
        BestMove = Move.Null;
        Pv = [];
        HashfullPermille = 0;
        TbHits = 0;
        RootScore = 0.0f;
    }
}
