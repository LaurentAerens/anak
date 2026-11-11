using Anak.Core;
using Anak.Search;
using Zanak.Time;

namespace Zanak.Search;

/// <summary>
/// Implements Anak's <see cref="ISearchEngine"/> interface, allowing Zanak (ZAP) to be selected
/// seamlessly inside Anak.UI and diagnostic benchmark harnesses.
/// </summary>
public sealed class ZapSearchEngine : ISearchEngine, IDisposable
{
    private readonly ZapSearch _search;
    private readonly ZapParams _params;
    private readonly Zanak.Time.TimeManager _timeManager;
    private readonly TranspositionTable _compatTt;

    public TranspositionTable TranspositionTable => _compatTt;

    /// <summary>
    /// Gets or sets the last move played on the board prior to searching, enabling recapture locality.
    /// </summary>
    public Move LastMove { get; set; } = Move.Null;

    public int Threads
    {
        get => _params.DefaultThreads;
        set => _params.DefaultThreads = Math.Max(1, value);
    }

    public ITablebaseProber? Tablebase => _search.Tablebase;

    public ZapSearchEngine(
        ZapParams? parameters = null,
        Zanak.Time.TimeManager? timeManager = null,
        IEvaluator? evaluator = null,
        ITablebaseProber? tablebase = null)
    {
        _params = parameters ?? ZapParams.Default;
        _timeManager = timeManager ?? new Zanak.Time.TimeManager();
        _search = new ZapSearch(_params, _timeManager, evaluator, tablebase);
        _compatTt = new TranspositionTable(16);
    }

    public Move Search(
        in Board board,
        in SearchLimits limits,
        Action<SearchStatistics>? infoCallback = null,
        CancellationToken cancellationToken = default)
    {
        int myTime = board.SideToMove == Color.White ? limits.WhiteTime : limits.BlackTime;
        int myInc = board.SideToMove == Color.White ? limits.WhiteIncrement : limits.BlackIncrement;

        SearchBudget budget = _timeManager.AllocateBudget(
            remainingTimeMs: myTime,
            incrementMs: myInc,
            movesToGo: limits.MovesToGo,
            moveTimeMs: limits.MoveTime,
            depthLimit: limits.Depth,
            infinite: limits.Infinite);

        Action<ZapSearchStats>? zapCallback = null;
        if (infoCallback != null)
        {
            zapCallback = stats =>
            {
                var searchStats = new SearchStatistics
                {
                    Depth = stats.Depth,
                    SelDepth = stats.Depth,
                    Nodes = (ulong)stats.Nodes,
                    TimeMs = stats.ElapsedMs,
                    Nps = (ulong)stats.Nps,
                    BestMove = stats.BestMove,
                    Pv = stats.Pv != null ? [.. stats.Pv] : [],
                    RootScore = stats.ScoreCp
                };

                // Populate TopMoves for UI candidate move display
                var topMoves = new List<RootMoveCandidate>();
                for (int i = 0; i < Math.Min(5, _search.SharedState.CandidateCount); i++)
                {
                    ZapRootCandidate cand = _search.SharedState.GetCandidate(i);
                    if (!cand.Move.IsNull)
                    {
                        List<Move>? pvLine = !cand.BestFollowup.IsNull ? [cand.Move, cand.BestFollowup] : [cand.Move];
                        topMoves.Add(new RootMoveCandidate(cand.Move, cand.Score, 0, pvLine));
                    }
                }
                searchStats.TopMoves = topMoves;

                infoCallback(searchStats);
            };
        }

        return _search.Search(in board, in budget, zapCallback, cancellationToken, lastMove: LastMove);
    }

    public void Clear()
    {
        LastMove = Move.Null;
        _search.Clear();
        _compatTt.Clear();
    }

    public void Stop()
    {
        _search.Stop();
    }

    public void Dispose()
    {
        _search.Dispose();
        _compatTt.Dispose();
    }
}
