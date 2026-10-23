using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Anak.Core;
using Anak.Search;
using File = System.IO.File;

namespace Anak.Protocol.Uci;

/// <summary>
/// Glues the UCI protocol commands to the underlying chess engine components:
/// Board state, MCTSEngine search, PolyGlot opening book, Syzygy tablebases, and evaluation.
/// </summary>
public sealed class UciEngineAdapter
{
    private readonly Action<string> _outputWriter;
    private readonly UciOptionRegistry _options;
    private readonly IEvaluator _evaluator;
    private readonly AlphaBetaEngine _alphaBetaEngine;
    private readonly MCTSEngine _mctsEngine;
    private ISearchEngine _activeEngine;
    private Board _board;
    private readonly List<Move> _gameMoves = new();
    private readonly Anak.Data.PolyGlot.PolyGlotBook _book = new();
    private readonly Anak.Data.Syzygy.SyzygyTablebase _tablebase = new();

    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;
    private long _searchSessionId;
    private readonly object _syncLock = new();

    public UciOptionRegistry Options => _options;
    public Board CurrentBoard => _board;
    public IReadOnlyList<Move> GameMoves => _gameMoves;
    public bool IsSearching => _searchTask != null && !_searchTask.IsCompleted;
    public IEvaluator Evaluator => _evaluator;
    public ISearchEngine ActiveEngine => _activeEngine;

    public UciEngineAdapter(Action<string> outputWriter)
    {
        _outputWriter = outputWriter ?? throw new ArgumentNullException(nameof(outputWriter));
        _options = new UciOptionRegistry();

        // Instantiate evaluation engine
        _evaluator = CreateEvaluator();

        // Instantiate search engines (AlphaBeta Minimax as default, MCTS as alternative)
        _alphaBetaEngine = new AlphaBetaEngine(_evaluator, defaultHashMb: _options.Hash.Value)
        {
            Threads = _options.Threads.Value
        };
        _mctsEngine = new MCTSEngine(_evaluator, defaultHashMb: _options.Hash.Value)
        {
            Threads = _options.Threads.Value
        };
        _activeEngine = _options.SearchAlgorithm.Value.Equals("Mcts", StringComparison.OrdinalIgnoreCase)
            ? _mctsEngine
            : _alphaBetaEngine;

        // Subscribe to UCI option change notifications
        _options.SearchAlgorithm.ValueChanged += alg =>
        {
            lock (_syncLock)
            {
                _activeEngine = alg.Equals("Mcts", StringComparison.OrdinalIgnoreCase)
                    ? _mctsEngine
                    : _alphaBetaEngine;
            }
        };
        _options.Hash.ValueChanged += OnHashChanged;
        _options.Threads.ValueChanged += OnThreadsChanged;


        // Wire opening book
        _book.LoadOrDefault(_options.BookFile.Value);
        _book.OwnBook = _options.OwnBook.Value;
        _book.Mode = ParseBookMode(_options.BookMode.Value);

        _options.BookFile.ValueChanged += path => _book.LoadOrDefault(path);
        _options.OwnBook.ValueChanged += enabled => _book.OwnBook = enabled;
        _options.BookMode.ValueChanged += modeStr => _book.Mode = ParseBookMode(modeStr);

        // Wire MCTS search randomness and temperature
        _mctsEngine.Parameters.MoveRandomness = _options.MoveRandomness.Value;
        _mctsEngine.Parameters.Temperature = _options.Temperature.Value / 100.0f;

        _options.MoveRandomness.ValueChanged += val => _mctsEngine.Parameters.MoveRandomness = val;
        _options.Temperature.ValueChanged += val => _mctsEngine.Parameters.Temperature = val / 100.0f;

        // Wire NNUE evaluator options
        if (_evaluator is Anak.Data.Nnue.MultiTierNnueEvaluator nnue)
        {
            nnue.UseNnue = _options.UseNnue.Value;
            nnue.NnueDirectory = _options.NnuePath.Value;
            nnue.BlunderWatchdog = _options.BlunderWatchdog.Value;
            nnue.LoadFromDirectory(_options.NnuePath.Value);

            _options.UseNnue.ValueChanged += val => nnue.UseNnue = val;
            _options.NnuePath.ValueChanged += path =>
            {
                nnue.NnueDirectory = path;
                nnue.LoadFromDirectory(path);
            };
            _options.BlunderWatchdog.ValueChanged += val => nnue.BlunderWatchdog = val;
        }

        // Wire Syzygy tablebases
        _tablebase.SyzygyPath = _options.SyzygyPath.Value;
        _tablebase.SyzygyProbeDepth = _options.SyzygyProbeDepth.Value;

        _options.SyzygyPath.ValueChanged += path => _tablebase.SyzygyPath = path;
        _options.SyzygyProbeDepth.ValueChanged += depth => _tablebase.SyzygyProbeDepth = depth;

        if (_evaluator is Anak.Data.Nnue.MultiTierNnueEvaluator nnueTb)
        {
            nnueTb.Tablebase = _tablebase;
        }

        // Wire AlphaBeta search options for SPSA tuning
        _alphaBetaEngine.LmrBase = _options.LmrBase.Value;
        _alphaBetaEngine.LmrDivision = _options.LmrDivision.Value;
        _alphaBetaEngine.RfpMargin = _options.RfpMargin.Value;
        _alphaBetaEngine.NmpBase = _options.NmpBase.Value;
        _alphaBetaEngine.AspirationDelta = _options.AspirationDelta.Value;

        _options.LmrBase.ValueChanged += val => _alphaBetaEngine.LmrBase = val;
        _options.LmrDivision.ValueChanged += val => _alphaBetaEngine.LmrDivision = val;
        _options.RfpMargin.ValueChanged += val => _alphaBetaEngine.RfpMargin = val;
        _options.NmpBase.ValueChanged += val => _alphaBetaEngine.NmpBase = val;
        _options.AspirationDelta.ValueChanged += val => _alphaBetaEngine.AspirationDelta = val;

        // Initialize board to standard start position
        _board = Fen.Parse(Fen.StartPosition);
    }

    private void OnHashChanged(int hashMb)
    {
        lock (_syncLock)
        {
            _alphaBetaEngine.TranspositionTable.Resize(hashMb);
            _mctsEngine.TranspositionTable.Resize(hashMb);
        }
    }

    private void OnThreadsChanged(int threads)
    {
        lock (_syncLock)
        {
            int t = Math.Max(1, threads);
            _alphaBetaEngine.Threads = t;
            _mctsEngine.Threads = t;
        }
    }


    /// <summary>
    /// Resets the engine state for a new game: clears transposition table,
    /// resets move history, and restores starting board position.
    /// </summary>
    public void NewGame()
    {
        lock (_syncLock)
        {
            StopSearchInternal();
            _alphaBetaEngine.TranspositionTable.Clear();
            _alphaBetaEngine.ClearHistory();
            _mctsEngine.TranspositionTable.Clear();
            _mctsEngine.ClearHistory();
            _gameMoves.Clear();
            _board = Fen.Parse(Fen.StartPosition);
        }
    }

    /// <summary>
    /// Sets the engine's current position from a FEN string (or startpos)
    /// and sequentially applies any subsequent UCI move strings.
    /// </summary>
    /// <param name="fen">FEN string, or null/empty/"startpos" for standard initial position.</param>
    /// <param name="moves">List of UCI move strings (e.g. "e2e4", "e7e5").</param>
    public void SetPosition(string? fen, IEnumerable<string>? moves)
    {
        lock (_syncLock)
        {
            StopSearchInternal();

            if (string.IsNullOrWhiteSpace(fen) || fen.Equals("startpos", StringComparison.OrdinalIgnoreCase))
            {
                _board = Fen.Parse(Fen.StartPosition);
            }
            else
            {
                _board = Fen.Parse(fen);
            }

            _gameMoves.Clear();

            if (moves != null)
            {
                foreach (var moveStr in moves)
                {
                    if (string.IsNullOrWhiteSpace(moveStr)) continue;

                    Move legalMove = ParseAndMatchMove(ref _board, moveStr);
                    if (!legalMove.IsNull)
                    {
                        _board.MakeMove(legalMove, out _);
                        _gameMoves.Add(legalMove);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Starts asynchronous search with the given search limits.
    /// </summary>
    /// <param name="limits">UCI search limits (clock, increment, depth, nodes, etc.).</param>
    public void StartSearch(SearchLimits limits)
    {
        lock (_syncLock)
        {
            StopSearchInternal();

            long currentSession = Interlocked.Increment(ref _searchSessionId);
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            // 1. Check opening book if enabled
            if (_options.OwnBook.Value && _book.TryGetMove(in _board, out Move bookMove))
            {
                _outputWriter($"info depth 1 score cp 0 pv {bookMove.ToUci()}");
                _outputWriter($"info string book hit {bookMove.ToUci()}");
                _outputWriter($"bestmove {bookMove.ToUci()}");
                return;
            }

            // 2. Check Syzygy tablebases at root
            if (BitOperations.PopCount(_board.Occupied) <= 6 && _tablebase.TryProbeDtz(in _board, out Move tbMove, out int dtzScore))
            {
                string wdlDesc = dtzScore > 0 ? "win" : (dtzScore < 0 ? "loss" : "draw");
                int scoreCp = dtzScore > 0 ? 20000 - dtzScore : (dtzScore < 0 ? -20000 - dtzScore : 0);
                _outputWriter($"info depth 1 score cp {scoreCp} tbhits {_tablebase.TbHits} pv {tbMove.ToUci()}");
                _outputWriter($"info string syzygy hit DTZ={dtzScore} ({wdlDesc})");
                _outputWriter($"bestmove {tbMove.ToUci()}");
                return;
            }

            // 3. Clone board for asynchronous search thread
            Board searchBoard = _board;

            _searchTask = Task.Run(() =>
            {
                try
                {
                    Move bestMove = _activeEngine.Search(
                        in searchBoard,
                        in limits,
                        stats =>
                        {
                            if (Interlocked.Read(ref _searchSessionId) == currentSession)
                            {
                                FormatAndSendInfo(stats);
                            }
                        },
                        token);

                    if (Interlocked.Read(ref _searchSessionId) == currentSession)
                    {
                        if (bestMove.IsNull)
                        {
                            MoveList legalMoves = default;
                            searchBoard.GenerateLegalMoves(ref legalMoves);
                            if (legalMoves.Count > 0)
                            {
                                bestMove = legalMoves[0];
                            }
                        }

                        string bestMoveStr = bestMove.IsNull ? "0000" : bestMove.ToUci();
                        _outputWriter($"bestmove {bestMoveStr}");
                    }
                }
                catch (OperationCanceledException)
                {
                    if (Interlocked.Read(ref _searchSessionId) == currentSession)
                    {
                        MoveList legalMoves = default;
                        searchBoard.GenerateLegalMoves(ref legalMoves);
                        string fallback = legalMoves.Count > 0 ? legalMoves[0].ToUci() : "0000";
                        _outputWriter($"bestmove {fallback}");
                    }
                }
                catch (Exception ex)
                {
                    if (Interlocked.Read(ref _searchSessionId) == currentSession)
                    {
                        _outputWriter($"info string search error: {ex.Message}");
                        MoveList legalMoves = default;
                        searchBoard.GenerateLegalMoves(ref legalMoves);
                        string fallback = legalMoves.Count > 0 ? legalMoves[0].ToUci() : "0000";
                        _outputWriter($"bestmove {fallback}");
                    }
                }
            }, token);
        }
    }

    /// <summary>
    /// Stops any ongoing search immediately.
    /// </summary>
    public void StopSearch()
    {
        lock (_syncLock)
        {
            StopSearchInternal();
        }
    }

    private void StopSearchInternal()
    {
        if (_searchCts != null)
        {
            _searchCts.Cancel();
            _searchCts.Dispose();
            _searchCts = null;
        }

        if (_searchTask != null)
        {
            try
            {
                // Await search task completion with a brief timeout to avoid deadlocks
                _searchTask.Wait(500);
            }
            catch
            {
                // Task cancellation or timeout exceptions are safely ignored
            }
            _searchTask = null;
        }
    }

    /// <summary>
    /// Transitions from ponder search to normal search.
    /// </summary>
    public void PonderHit()
    {
        // When ponderhit arrives, search continues normally under existing clock limits
    }

    /// <summary>
    /// Formats search statistics into a standard UCI 'info' line and writes it.
    /// </summary>
    private void FormatAndSendInfo(SearchStatistics stats)
    {
        string scoreStr;
        if (stats.MateIn != 0)
        {
            scoreStr = $"mate {stats.MateIn}";
        }
        else if (MathF.Abs(stats.RootScore) > 0.98f)
        {
            int mateMoves = stats.RootScore > 0 ? 1 : -1;
            scoreStr = $"mate {mateMoves}";
        }
        else if (stats.ScoreCp != 0)
        {
            scoreStr = $"cp {stats.ScoreCp}";
        }
        else
        {
            float clamped = Math.Clamp(stats.RootScore, -0.999f, 0.999f);
            int cp = (int)(MathF.Atanh(clamped) * 400.0f);
            scoreStr = $"cp {cp}";
        }

        string pvStr = string.Empty;
        if (stats.Pv != null && stats.Pv.Count > 0)
        {
            pvStr = " pv " + string.Join(' ', stats.Pv.Select(m => m.ToUci()));
        }
        else if (!stats.BestMove.IsNull)
        {
            pvStr = $" pv {stats.BestMove.ToUci()}";
        }

        string infoLine = $"info depth {stats.Depth} seldepth {stats.SelDepth} score {scoreStr} nodes {stats.Nodes} nps {stats.Nps} time {stats.TimeMs} hashfull {stats.HashfullPermille} tbhits {stats.TbHits}{pvStr}";
        _outputWriter(infoLine);
    }

    /// <summary>
    /// Parses a UCI move string (e.g. "e2e4", "e7e8q") and matches it against
    /// the board's legal moves to produce a fully validated Move struct.
    /// </summary>
    public static Move ParseAndMatchMove(ref Board board, ReadOnlySpan<char> moveStr)
    {
        if (moveStr.Length < 4) return Move.Null;

        Square from = SquareExtensions.Parse(moveStr[..2]);
        Square to = SquareExtensions.Parse(moveStr.Slice(2, 2));

        PieceType promo = PieceType.None;
        if (moveStr.Length >= 5)
        {
            promo = char.ToLowerInvariant(moveStr[4]) switch
            {
                'n' => PieceType.Knight,
                'b' => PieceType.Bishop,
                'r' => PieceType.Rook,
                'q' => PieceType.Queen,
                _ => PieceType.None
            };
        }

        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        for (int i = 0; i < moves.Count; i++)
        {
            Move candidate = moves[i];
            if (candidate.From == from && candidate.To == to)
            {
                if (promo != PieceType.None)
                {
                    if (candidate.PromotionPieceType == promo) return candidate;
                }
                else
                {
                    return candidate;
                }
            }
        }

        return Move.Null;
    }

    private static Anak.Data.PolyGlot.BookMode ParseBookMode(string modeStr) => modeStr switch
    {
        "BestMove" => Anak.Data.PolyGlot.BookMode.BestMove,
        "UniformRandom" => Anak.Data.PolyGlot.BookMode.UniformRandom,
        _ => Anak.Data.PolyGlot.BookMode.WeightedRandom
    };

    /// <summary>
    /// Factory for the evaluation engine: tries to resolve MultiTierNnueEvaluator from Anak.Data,
    /// or falls back to an internal high-performance material evaluator.
    /// </summary>
    public static IEvaluator CreateEvaluator()
    {
        try
        {
            return new Anak.Data.Nnue.MultiTierNnueEvaluator();
        }
        catch
        {
            return new InternalMaterialEvaluator();
        }
    }
}

/// <summary>
/// Fast zero-allocation fallback material and mobility evaluator implementing IEvaluator.
/// </summary>
public sealed class InternalMaterialEvaluator : IEvaluator
{
    private const int PawnVal = 100;
    private const int KnightVal = 320;
    private const int BishopVal = 330;
    private const int RookVal = 500;
    private const int QueenVal = 900;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves)
    {
        // 1. Material score
        int whiteMaterial =
            BitOperations.PopCount(board.PieceMaskPawn & board.ColorMaskWhite) * PawnVal +
            BitOperations.PopCount(board.PieceMaskKnight & board.ColorMaskWhite) * KnightVal +
            BitOperations.PopCount(board.PieceMaskBishop & board.ColorMaskWhite) * BishopVal +
            BitOperations.PopCount(board.PieceMaskRook & board.ColorMaskWhite) * RookVal +
            BitOperations.PopCount(board.PieceMaskQueen & board.ColorMaskWhite) * QueenVal;

        int blackMaterial =
            BitOperations.PopCount(board.PieceMaskPawn & board.ColorMaskBlack) * PawnVal +
            BitOperations.PopCount(board.PieceMaskKnight & board.ColorMaskBlack) * KnightVal +
            BitOperations.PopCount(board.PieceMaskBishop & board.ColorMaskBlack) * BishopVal +
            BitOperations.PopCount(board.PieceMaskRook & board.ColorMaskBlack) * RookVal +
            BitOperations.PopCount(board.PieceMaskQueen & board.ColorMaskBlack) * QueenVal;

        // 2. Mobility bonus (centipawns per legal move for side to move)
        int mobilityScore = legalMoves.Length * 4;

        int sidePerspective = board.SideToMove == Color.White
            ? (whiteMaterial - blackMaterial) + mobilityScore
            : (blackMaterial - whiteMaterial) + mobilityScore;

        return new Value(sidePerspective);
    }
}
