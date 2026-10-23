using System.Numerics;
using Anak.Core;
using Anak.Data.Datagen;
using Anak.Data.Syzygy;
using Anak.Protocol.Uci;
using Anak.Search;

namespace Anak.Protocol.Datagen;

/// <summary>
/// Autonomous multi-threaded datagen worker.
/// Generates self-play games, executes Alpha-Beta searches with Syzygy adjudication,
/// extracts tactical volatility swings, and streams positions to the stratified writer.
/// </summary>
public sealed class DatagenWorker
{
    private readonly int _workerId;
    private readonly DatagenOptions _options;
    private readonly StratifiedBinpackWriter _writer;
    private readonly Random _rng;
    private readonly IEvaluator _evaluator;
    private readonly AlphaBetaEngine _engine;
    private readonly SyzygyTablebase? _syzygy;

    public int WorkerId => _workerId;

    public DatagenWorker(int workerId, DatagenOptions options, StratifiedBinpackWriter writer)
    {
        _workerId = workerId;
        _options = options;
        _writer = writer;
        _rng = new Random(HashCode.Combine(Environment.TickCount, workerId, Guid.NewGuid().GetHashCode()));

        _evaluator = UciEngineAdapter.CreateEvaluator();
        _engine = new AlphaBetaEngine(_evaluator, defaultHashMb: 16);

        if (!string.IsNullOrEmpty(options.SyzygyPath))
        {
            _syzygy = new SyzygyTablebase(options.SyzygyPath);
        }
    }

    /// <summary>
    /// Executes a single self-play game and writes all generated positions with terminal WDL.
    /// Returns the game outcome (1.0 = WhiteWin, 0.5 = Draw, 0.0 = BlackWin), or null if aborted.
    /// </summary>
    public float? PlayOneGame(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;

        var board = Fen.Parse(Fen.StartPosition);
        var gamePositions = new List<(Board Board, int Score, bool IsVolatile)>();

        // 1. Random Opening Plies for Diversity
        int randomPlies = _options.RandomPlies;
        for (int p = 0; p < randomPlies; p++)
        {
            if (ct.IsCancellationRequested) return null;

            MoveList legals = default;
            MoveGenerator.GenerateLegalMoves(in board, ref legals);
            if (legals.Count == 0) return null; // Aborted early

            Move rndMove = legals[_rng.Next(legals.Count)];
            board.MakeMove(rndMove, out _);
        }

        // 2. Self-Play Search Loop
        int consecutiveDominance = 0;
        int consecutiveDrawish = 0;
        float whiteWdl = 0.5f;
        bool gameTerminated = false;

        var searchLimits = new SearchLimits
        {
            Depth = _options.Depth,
            Nodes = (ulong)Math.Max(1, _options.Nodes)
        };

        for (int ply = 0; ply < _options.MaxPliesPerGame; ply++)
        {
            if (ct.IsCancellationRequested) return null;

            // Check standard game over
            if (board.IsGameOver(out GameResult overResult))
            {
                whiteWdl = overResult switch
                {
                    GameResult.Checkmate => board.SideToMove == Color.White ? 0.0f : 1.0f,
                    _ => 0.5f
                };
                gameTerminated = true;
                break;
            }

            // Check Syzygy tablebase adjudication for endgames (<= 6 pieces)
            if (_syzygy != null && BitOperations.PopCount(board.Occupied) <= 6)
            {
                if (_syzygy.TryProbeWdl(in board, out int tbWdl))
                {
                    if (tbWdl > 0)
                    {
                        whiteWdl = board.SideToMove == Color.White ? 1.0f : 0.0f;
                    }
                    else if (tbWdl < 0)
                    {
                        whiteWdl = board.SideToMove == Color.White ? 0.0f : 1.0f;
                    }
                    else
                    {
                        whiteWdl = 0.5f;
                    }
                    gameTerminated = true;
                    break;
                }
            }

            // Engine Search
            Move bestMove = _engine.Search(in board, in searchLimits, cancellationToken: ct);
            if (bestMove.IsNull || ct.IsCancellationRequested)
            {
                break;
            }

            int score = _engine.LastScoreCp;

            // Detect Blunder / Volatility swing (>= 150 cp swing relative to previous ply)
            bool isVolatile = false;
            if (gamePositions.Count > 0)
            {
                int prevScore = gamePositions[^1].Score;
                isVolatile = Math.Abs(score - (-prevScore)) >= 150;
            }

            // Adjudication 1: Resignation threshold (Dominance >= 1000 cp for 3 consecutive plies)
            if (Math.Abs(score) >= 1000)
            {
                consecutiveDominance++;
                if (consecutiveDominance >= 3)
                {
                    bool sideWon = score > 0;
                    whiteWdl = (board.SideToMove == Color.White) == sideWon ? 1.0f : 0.0f;
                    gameTerminated = true;
                    break;
                }
            }
            else
            {
                consecutiveDominance = 0;
            }

            // Adjudication 2: Draw threshold (Drawish score <= 10 cp for 10 plies after ply 40)
            if (ply >= 40 && Math.Abs(score) <= 10)
            {
                consecutiveDrawish++;
                if (consecutiveDrawish >= 10)
                {
                    whiteWdl = 0.5f;
                    gameTerminated = true;
                    break;
                }
            }
            else
            {
                consecutiveDrawish = 0;
            }

            // Record position for datagen
            gamePositions.Add((board, score, isVolatile));

            // Execute move
            board.MakeMove(bestMove, out _);
        }

        if (!gameTerminated && gamePositions.Count < 10)
        {
            return null; // Discard degenerate / truncated games
        }

        // 3. Backpropagate WDL to positions and stream to Stratified Writer
        foreach (var pos in gamePositions)
        {
            float targetWdl = pos.Board.SideToMove == Color.White ? whiteWdl : (1.0f - whiteWdl);
            _writer.WritePosition(in pos.Board, pos.Score, targetWdl, pos.IsVolatile);
        }

        return whiteWdl;
    }
}
