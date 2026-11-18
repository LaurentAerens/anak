using System.Runtime.CompilerServices;
using Anak.Core;
using Zanak.Search;
using Zanak.Time;

namespace Zanak.Uci;

/// <summary>
/// Full asynchronous UCI protocol loop and command coordinator for Zanak.
/// </summary>
public sealed class UciHandler : IDisposable
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly object _outputLock = new();
    private readonly object _stateLock = new();

    private readonly ZapParams _params = new();
    private readonly TimeParams _timeParams = new();
    private readonly TimeManager _timeManager;
    private readonly ZapSearch _search;
    private readonly Dictionary<string, Action<string>> _optionHandlers;

    private Board _board;
    private Move _lastMove = Move.Null;
    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;
    private string? _nnueError;

    /// <summary>
    /// Initializes a new <see cref="UciHandler"/> reading commands from <paramref name="input"/> and writing responses to <paramref name="output"/>.
    /// </summary>
    public UciHandler(TextReader input, TextWriter output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));

        _timeManager = new TimeManager(_timeParams);
        _search = new ZapSearch(_params, _timeManager);
        _board = Fen.Parse(Fen.StartPosition);
        _optionHandlers = BuildOptionHandlers();
    }

    /// <summary>
    /// Writes a single UCI response line to the output stream in a thread-safe manner.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendLine(string line)
    {
        lock (_outputLock)
        {
            _output.WriteLine(line);
            _output.Flush();
        }
    }

    /// <summary>
    /// Reads and dispatches UCI commands until <c>quit</c>, EOF, or cancellation.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? rawLine = await _input.ReadLineAsync(cancellationToken);
            if (rawLine == null) break;

            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            bool shouldQuit = HandleCommand(line);
            if (shouldQuit) break;
        }

        StopSearch();
    }

    /// <summary>
    /// Parses and executes a single UCI command line. Returns <c>true</c> if <c>quit</c> was received.
    /// </summary>
    public bool HandleCommand(string line)
    {
        ReadOnlySpan<char> span = line.AsSpan().Trim();
        if (span.IsEmpty) return false;

        int firstSpace = span.IndexOf(' ');
        ReadOnlySpan<char> cmd = firstSpace >= 0 ? span[..firstSpace] : span;
        ReadOnlySpan<char> args = firstSpace >= 0 ? span[(firstSpace + 1)..].Trim() : ReadOnlySpan<char>.Empty;

        if (cmd.Equals("uci", StringComparison.OrdinalIgnoreCase))
        {
            HandleUci();
        }
        else if (cmd.Equals("isready", StringComparison.OrdinalIgnoreCase))
        {
            bool isSearching;
            lock (_stateLock)
            {
                isSearching = _searchTask is { IsCompleted: false };
            }

            if (!isSearching && _nnueError == null)
            {
                _search.EnsureWarmedUp();
            }

            SendLine("readyok");
        }
        else if (cmd.Equals("ucinewgame", StringComparison.OrdinalIgnoreCase))
        {
            HandleUciNewGame();
        }
        else if (cmd.Equals("position", StringComparison.OrdinalIgnoreCase))
        {
            HandlePosition(args.ToString());
        }
        else if (cmd.Equals("go", StringComparison.OrdinalIgnoreCase))
        {
            HandleGo(args.ToString());
        }
        else if (cmd.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            StopSearch();
        }
        else if (cmd.Equals("setoption", StringComparison.OrdinalIgnoreCase))
        {
            HandleSetOption(args.ToString());
        }
        else if (cmd.Equals("quit", StringComparison.OrdinalIgnoreCase))
        {
            StopSearch();
            return true;
        }

        return false;
    }

    private void HandleUci()
    {
        SendLine("id name Zanak 1.0.0");
        SendLine("id author Anak Team");
        SendLine($"option name Threads type spin default {_params.DefaultThreads} min 1 max 128");
        SendLine("option name Hash type spin default 64 min 1 max 65536");
        SendLine("option name SearchTraceFile type string default <empty>");
        SendLine("option name PickupTraceFile type string default <empty>");
        SendLine($"option name InterleaveRootSeeding type check default {_params.InterleaveRootSeeding.ToString().ToLowerInvariant()}");
        SendLine($"option name PickupCostWeight type spin default {_params.CrosspointCostWeight} min 0 max 100");
        SendLine($"option name NnuePath type string default {_search.NnueEvaluator?.NnueDirectory}");
        SendLine($"option name ShockThreshold type spin default {_params.ShockThresholdCp} min 10 max 500");
        SendLine($"option name DeadStemMargin type spin default {_params.DeadStemPruneMarginCp} min 50 max 1000");
        SendLine($"option name HardAspirationMargin type spin default {_params.HardAspirationMarginCp} min 20 max 500");
        SendLine($"option name ContenderPromotionMargin type spin default {_params.ContenderPromotionMarginCp} min 10 max 300");
        SendLine($"option name WorkerPruneMargin type spin default {_params.WorkerPruneMarginCp} min 50 max 1000");
        SendLine($"option name AdaptiveShockBase type spin default {_params.AdaptiveShockBaseCp} min 10 max 200");
        SendLine($"option name AdaptiveShockSlope type spin default {_params.AdaptiveShockSlopeCp} min 0 max 50");
        SendLine($"option name RecaptureBonus type spin default {_params.RecaptureBonus} min 0 max 10000");
        SendLine($"option name EnPriseThreatPenalty type spin default {_params.EnPriseThreatPenalty} min 0 max 15000");
        SendLine($"option name SpineAbandonThreshold type spin default {_params.SpineAbandonThresholdCp} min 50 max 1000");
        SendLine($"option name CrosspointRootBiasWeight type spin default {_params.CrosspointRootBiasWeight} min 0 max 100");
        SendLine($"option name MaxSiblingExploration type spin default {_params.MaxSiblingExploration} min 1 max 32");

        // TimeParams options
        SendLine($"option name HyperBulletBaseDepth type spin default {_timeParams.HyperBulletBaseDepth} min 1 max 10");
        SendLine($"option name HyperBulletMaxHorizon type spin default {_timeParams.HyperBulletMaxHorizon} min 2 max 15");
        SendLine($"option name FastBulletBaseDepth type spin default {_timeParams.FastBulletBaseDepth} min 1 max 12");
        SendLine($"option name FastBulletMaxHorizon type spin default {_timeParams.FastBulletMaxHorizon} min 2 max 20");
        SendLine($"option name BulletBaseDepth type spin default {_timeParams.BulletBaseDepth} min 2 max 14");
        SendLine($"option name BulletMaxHorizon type spin default {_timeParams.BulletMaxHorizon} min 4 max 24");
        SendLine($"option name BlitzBaseDepth type spin default {_timeParams.BlitzBaseDepth} min 2 max 16");
        SendLine($"option name BlitzMaxHorizon type spin default {_timeParams.BlitzMaxHorizon} min 4 max 26");
        SendLine($"option name RapidBaseDepth type spin default {_timeParams.RapidBaseDepth} min 4 max 20");
        SendLine($"option name RapidMaxHorizon type spin default {_timeParams.RapidMaxHorizon} min 6 max 32");
        SendLine($"option name AbsoluteMinBaseDepth type spin default {_timeParams.AbsoluteMinBaseDepth} min 1 max 10");
        SendLine($"option name AbsoluteMinMaxHorizon type spin default {_timeParams.AbsoluteMinMaxHorizon} min 2 max 16");
        SendLine($"option name SuddenDeathDivisor type spin default {(int)_timeParams.SuddenDeathDivisor} min 10 max 60");
        SendLine($"option name SuddenDeathIncFactor type spin default {(int)(_timeParams.SuddenDeathIncFactor * 100)} min 10 max 100");
        SendLine($"option name HardLimitMultiplier type spin default {(int)(_timeParams.HardLimitMultiplier * 10)} min 10 max 60");
        SendLine($"option name StableDominanceMinDepth type spin default {_timeParams.StableDominanceMinDepth} min 3 max 14");
        SendLine($"option name StableDominanceLeadCp type spin default {_timeParams.StableDominanceLeadCp} min 40 max 400");
        SendLine($"option name StableDominanceIters type spin default {_timeParams.StableDominanceIters} min 1 max 8");
        SendLine($"option name StableDominanceMinSoftPct type spin default {(int)Math.Round(_timeParams.StableDominanceMinSoftFraction * 100)} min 5 max 70");
        SendLine($"option name PanicExtensionGapCp type spin default {_timeParams.PanicExtensionGapCp} min 0 max 60");
        SendLine($"option name PanicExtensionFactorPct type spin default {(int)Math.Round(_timeParams.PanicExtensionFactor * 100)} min 100 max 250");
        SendLine($"option name SurvivorSoftScale2Pct type spin default {(int)Math.Round(_timeParams.SurvivorSoftScale2 * 100)} min 10 max 95");
        SendLine($"option name SurvivorSoftScale3Pct type spin default {(int)Math.Round(_timeParams.SurvivorSoftScale3 * 100)} min 15 max 95");
        SendLine($"option name SurvivorSoftScale4Pct type spin default {(int)Math.Round(_timeParams.SurvivorSoftScale4 * 100)} min 25 max 100");
        SendLine($"option name SmallSurvivorMinSoftPct type spin default {(int)Math.Round(_timeParams.SmallSurvivorMinSoftFraction * 100)} min 2 max 50");
        SendLine($"option name DominantLeaderLeadCp type spin default {_timeParams.DominantLeaderLeadCp} min 40 max 400");
        SendLine($"option name SmallSurvivorVerifyCrosspoints type spin default {_timeParams.SmallSurvivorVerifyCrosspoints} min 2 max 20");
        SendLine($"option name LeaderVerifyCrosspoints type spin default {_timeParams.LeaderVerifyCrosspoints} min 4 max 35");
        SendLine($"option name ContenderQuickCheckPerRoot type spin default {_timeParams.ContenderQuickCheckPerRoot} min 1 max 10");
        SendLine($"option name ClassicalMaxBaseDepth type spin default {_timeParams.ClassicalMaxBaseDepth} min 6 max 24");
        SendLine($"option name ClassicalHorizonOffset type spin default {_timeParams.ClassicalHorizonOffset} min 2 max 16");
        SendLine($"option name ExtendedHorizonMaxPlies type spin default {_timeParams.ExtendedHorizonMaxPlies} min 10 max 40");

        string syzygyPath = _search.Tablebase is Anak.Data.Syzygy.SyzygyTablebase stb ? stb.SyzygyPath : "<empty>";
        SendLine($"option name SyzygyPath type string default {syzygyPath}");
        SendLine($"option name SyzygyProbeDepth type spin default {_params.SyzygyProbeDepth} min 1 max 100");
        SendLine("uciok");
    }

    private void HandleUciNewGame()
    {
        lock (_stateLock)
        {
            StopSearch();
            _search.TranspositionTable.Clear();
            _board = Fen.Parse(Fen.StartPosition);
            _lastMove = Move.Null;
        }
    }

    private void HandleSetOption(string args)
    {
        string[] tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int nameIdx = Array.FindIndex(tokens, t => t.Equals("name", StringComparison.OrdinalIgnoreCase));
        int valIdx = Array.FindIndex(tokens, t => t.Equals("value", StringComparison.OrdinalIgnoreCase));

        if (nameIdx >= 0 && valIdx > nameIdx)
        {
            StopSearch();
            string optName = string.Join(' ', tokens[(nameIdx + 1)..valIdx]);
            string optVal = string.Join(' ', tokens[(valIdx + 1)..]);

            if (_optionHandlers.TryGetValue(optName, out Action<string>? handler))
            {
                handler(optVal);
            }
        }
    }

    private Dictionary<string, Action<string>> BuildOptionHandlers()
    {
        static Action<string> IntOpt(Action<int> setter) => v => { if (int.TryParse(v, out int x)) setter(x); };
        static Action<string> ClampedIntOpt(Action<int> setter, int min, int max) =>
            v => { if (int.TryParse(v, out int x)) setter(Math.Clamp(x, min, max)); };
        static Action<string> ClampedPctOpt(Action<double> setter, int min, int max) =>
            v => { if (int.TryParse(v, out int x)) setter(Math.Clamp(x, min, max) / 100.0); };
        static Action<string> DoubleOpt(Action<double> setter) => v => { if (double.TryParse(v, out double x)) setter(x); };

        return new Dictionary<string, Action<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SearchTraceFile"] = v => _search.SearchTraceFile = v == "<empty>" ? string.Empty : v,
            ["PickupTraceFile"] = v => _search.PickupTraceFile = v == "<empty>" ? string.Empty : v,
            ["InterleaveRootSeeding"] = v => { if (bool.TryParse(v, out bool b)) _params.InterleaveRootSeeding = b; },
            ["PickupCostWeight"] = IntOpt(x => _params.CrosspointCostWeight = Math.Clamp(x, 0, 100)),
            ["NnuePath"] = v =>
            {
                try
                {
                    _search.SetNnuePath(v);
                    _nnueError = null;
                    SendLine($"info string NNUE active directory {_search.NnueEvaluator!.NnueDirectory}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    _nnueError = ex.Message;
                    SendLine($"info string NNUE configuration error: {_nnueError}");
                }
            },
            ["Threads"] = IntOpt(_search.SetWorkerThreads),
            ["Hash"] = IntOpt(_search.TranspositionTable.Resize),
            ["ShockThreshold"] = IntOpt(x => _params.ShockThresholdCp = x),
            ["DeadStemMargin"] = IntOpt(x => _params.DeadStemPruneMarginCp = x),
            ["HardAspirationMargin"] = IntOpt(x => _params.HardAspirationMarginCp = x),
            ["ContenderPromotionMargin"] = IntOpt(x => _params.ContenderPromotionMarginCp = x),
            ["WorkerPruneMargin"] = IntOpt(x => _params.WorkerPruneMarginCp = x),
            ["AdaptiveShockBase"] = IntOpt(x => _params.AdaptiveShockBaseCp = x),
            ["AdaptiveShockSlope"] = IntOpt(x => _params.AdaptiveShockSlopeCp = x),
            ["RecaptureBonus"] = IntOpt(x => _params.RecaptureBonus = x),
            ["EnPriseThreatPenalty"] = IntOpt(x => _params.EnPriseThreatPenalty = x),
            ["SpineAbandonThreshold"] = IntOpt(x => _params.SpineAbandonThresholdCp = x),
            ["CrosspointRootBiasWeight"] = IntOpt(x => _params.CrosspointRootBiasWeight = x),
            ["MaxSiblingExploration"] = ClampedIntOpt(x => _params.MaxSiblingExploration = x, 1, 32),
            ["HyperBulletBaseDepth"] = IntOpt(x => _timeParams.HyperBulletBaseDepth = x),
            ["HyperBulletMaxHorizon"] = IntOpt(x => _timeParams.HyperBulletMaxHorizon = x),
            ["FastBulletBaseDepth"] = IntOpt(x => _timeParams.FastBulletBaseDepth = x),
            ["FastBulletMaxHorizon"] = IntOpt(x => _timeParams.FastBulletMaxHorizon = x),
            ["BulletBaseDepth"] = IntOpt(x => _timeParams.BulletBaseDepth = x),
            ["BulletMaxHorizon"] = IntOpt(x => _timeParams.BulletMaxHorizon = x),
            ["BlitzBaseDepth"] = IntOpt(x => _timeParams.BlitzBaseDepth = x),
            ["BlitzMaxHorizon"] = IntOpt(x => _timeParams.BlitzMaxHorizon = x),
            ["RapidBaseDepth"] = IntOpt(x => _timeParams.RapidBaseDepth = x),
            ["RapidMaxHorizon"] = IntOpt(x => _timeParams.RapidMaxHorizon = x),
            ["AbsoluteMinBaseDepth"] = IntOpt(x => _timeParams.AbsoluteMinBaseDepth = x),
            ["AbsoluteMinMaxHorizon"] = IntOpt(x => _timeParams.AbsoluteMinMaxHorizon = x),
            ["SuddenDeathDivisor"] = DoubleOpt(x => _timeParams.SuddenDeathDivisor = x),
            ["SuddenDeathIncFactor"] = DoubleOpt(x => _timeParams.SuddenDeathIncFactor = x > 1.0 ? x / 100.0 : x),
            ["HardLimitMultiplier"] = DoubleOpt(x => _timeParams.HardLimitMultiplier = x > 10.0 ? x / 10.0 : x),
            ["StableDominanceMinDepth"] = ClampedIntOpt(x => _timeParams.StableDominanceMinDepth = x, 3, 14),
            ["StableDominanceLeadCp"] = ClampedIntOpt(x => _timeParams.StableDominanceLeadCp = x, 40, 400),
            ["StableDominanceIters"] = ClampedIntOpt(x => _timeParams.StableDominanceIters = x, 1, 8),
            ["StableDominanceMinSoftPct"] = ClampedPctOpt(x => _timeParams.StableDominanceMinSoftFraction = x, 5, 70),
            ["PanicExtensionGapCp"] = ClampedIntOpt(x => _timeParams.PanicExtensionGapCp = x, 0, 60),
            ["PanicExtensionFactorPct"] = ClampedPctOpt(x => _timeParams.PanicExtensionFactor = x, 100, 250),
            ["SurvivorSoftScale2Pct"] = ClampedPctOpt(x => _timeParams.SurvivorSoftScale2 = x, 10, 95),
            ["SurvivorSoftScale3Pct"] = ClampedPctOpt(x => _timeParams.SurvivorSoftScale3 = x, 15, 95),
            ["SurvivorSoftScale4Pct"] = ClampedPctOpt(x => _timeParams.SurvivorSoftScale4 = x, 25, 100),
            ["SmallSurvivorMinSoftPct"] = ClampedPctOpt(x => _timeParams.SmallSurvivorMinSoftFraction = x, 2, 50),
            ["DominantLeaderLeadCp"] = ClampedIntOpt(x => _timeParams.DominantLeaderLeadCp = x, 40, 400),
            ["SmallSurvivorVerifyCrosspoints"] = ClampedIntOpt(x => _timeParams.SmallSurvivorVerifyCrosspoints = x, 2, 20),
            ["LeaderVerifyCrosspoints"] = ClampedIntOpt(x => _timeParams.LeaderVerifyCrosspoints = x, 4, 35),
            ["ContenderQuickCheckPerRoot"] = ClampedIntOpt(x => _timeParams.ContenderQuickCheckPerRoot = x, 1, 10),
            ["ClassicalMaxBaseDepth"] = ClampedIntOpt(x => _timeParams.ClassicalMaxBaseDepth = x, 6, 24),
            ["ClassicalHorizonOffset"] = ClampedIntOpt(x => _timeParams.ClassicalHorizonOffset = x, 2, 16),
            ["ExtendedHorizonMaxPlies"] = ClampedIntOpt(x => _timeParams.ExtendedHorizonMaxPlies = x, 10, 40),
            ["SyzygyPath"] = v =>
            {
                if (_search.Tablebase is Anak.Data.Syzygy.SyzygyTablebase stb)
                {
                    stb.SyzygyPath = v;
                }
            },
            ["SyzygyProbeDepth"] = IntOpt(x =>
            {
                _params.SyzygyProbeDepth = x;
                if (_search.Tablebase != null)
                {
                    _search.Tablebase.SyzygyProbeDepth = x;
                }
            }),
        };
    }

    private void HandlePosition(string args)
    {
        lock (_stateLock)
        {
            StopSearch();

            string[] tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return;

            int movesIdx = Array.FindIndex(tokens, t => t.Equals("moves", StringComparison.OrdinalIgnoreCase));
            _lastMove = Move.Null;

            if (tokens[0].Equals("startpos", StringComparison.OrdinalIgnoreCase))
            {
                _board = Fen.Parse(Fen.StartPosition);
            }
            else if (tokens[0].Equals("fen", StringComparison.OrdinalIgnoreCase))
            {
                int fenEnd = movesIdx >= 0 ? movesIdx : tokens.Length;
                string fenStr = string.Join(' ', tokens[1..fenEnd]);
                _board = Fen.Parse(fenStr);
            }

            if (movesIdx >= 0 && movesIdx + 1 < tokens.Length)
            {
                for (int i = movesIdx + 1; i < tokens.Length; i++)
                {
                    Move legalMove = MatchLegalMove(ref _board, tokens[i]);
                    if (!legalMove.IsNull)
                    {
                        _board.MakeMove(legalMove, out _);
                        _lastMove = legalMove;
                    }
                }
            }
        }
    }

    private static Move MatchLegalMove(ref Board board, string moveStr)
    {
        if (moveStr.Length < 4) return Move.Null;

        Square from = SquareExtensions.Parse(moveStr.AsSpan(0, 2));
        Square to = SquareExtensions.Parse(moveStr.AsSpan(2, 2));

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

        MoveList legal = default;
        MoveGenerator.GenerateLegalMoves(in board, ref legal);

        for (int i = 0; i < legal.Count; i++)
        {
            Move m = legal[i];
            if (m.From == from && m.To == to)
            {
                if (promo == PieceType.None || m.PromotionPieceType == promo)
                {
                    return m;
                }
            }
        }

        return Move.Null;
    }

    private void HandleGo(string args)
    {
        lock (_stateLock)
        {
            StopSearch();

            if (_nnueError != null)
            {
                SendLine($"info string Search refused: NNUE configuration error: {_nnueError}");
                return;
            }

            int wtime = 0, btime = 0, winc = 0, binc = 0, movestogo = 0, movetime = 0, depth = 0;
            bool infinite = false;

            string[] tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string tok = tokens[i].ToLowerInvariant();
                if (tok == "wtime" && i + 1 < tokens.Length) int.TryParse(tokens[++i], out wtime);
                else if (tok == "btime" && i + 1 < tokens.Length) int.TryParse(tokens[++i], out btime);
                else if (tok == "winc" && i + 1 < tokens.Length) int.TryParse(tokens[++i], out winc);
                else if (tok == "binc" && i + 1 < tokens.Length) int.TryParse(tokens[++i], out binc);
                else if (tok == "movestogo" && i + 1 < tokens.Length) int.TryParse(tokens[++i], out movestogo);
                else if (tok == "movetime" && i + 1 < tokens.Length) int.TryParse(tokens[++i], out movetime);
                else if (tok == "depth" && i + 1 < tokens.Length) int.TryParse(tokens[++i], out depth);
                else if (tok == "infinite") infinite = true;
            }

            int myTime = _board.SideToMove == Color.White ? wtime : btime;
            int myInc = _board.SideToMove == Color.White ? winc : binc;

            SearchBudget budget = _timeManager.AllocateBudget(
                remainingTimeMs: myTime,
                incrementMs: myInc,
                movesToGo: movestogo,
                moveTimeMs: movetime,
                depthLimit: depth,
                infinite: infinite,
                threads: _params.DefaultThreads);

            Board searchBoard = _board;
            Move lastMove = _lastMove;
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            _searchTask = Task.Run(() =>
            {
                try
                {
                    if (_search.NnueEvaluator is { } nnue)
                    {
                        nnue.SetRootTopology(in searchBoard);
                        SendLine($"info string NNUE active directory {nnue.NnueDirectory} expert {nnue.ActiveRootExpert}");
                    }
                    Move bestMove = _search.Search(
                        in searchBoard,
                        in budget,
                        stats => SendLine(UciOutput.FormatInfo(in stats)),
                        token,
                        lastMove);

                    if (_search.LastSearchDiagnostics.Length > 0)
                    {
                        SendLine($"info string {_search.LastSearchDiagnostics}");
                    }
                    SendLine(UciOutput.FormatBestMove(bestMove));
                }
                catch (OperationCanceledException)
                {
                    // Clean search abort
                }
                catch (Exception ex)
                {
                    SendLine($"info string Exception: {ex}");
                }
            }, string.IsNullOrEmpty(_search.SearchTraceFile) || _search.SearchTraceFile == "<empty>"
                ? token : CancellationToken.None);
        }
    }

    /// <summary>
    /// Cancels any active search and waits for the background search task to finish.
    /// </summary>
    public void StopSearch()
    {
        lock (_stateLock)
        {
            if (_searchCts != null && !_searchCts.IsCancellationRequested)
            {
                _searchCts.Cancel();
            }
            _search.Stop();

            if (_searchTask != null)
            {
                try
                {
                    _searchTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // Ignore task cancellation exceptions
                }
                _searchTask = null;
            }
            _searchCts?.Dispose();
            _searchCts = null;
        }
    }

    /// <summary>
    /// Stops any active search and disposes the underlying search engine.
    /// </summary>
    public void Dispose()
    {
        StopSearch();
        _search.Dispose();
    }
}
