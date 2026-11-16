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

    private Board _board;
    private Move _lastMove = Move.Null;
    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;
    private string? _nnueError;

    public UciHandler(TextReader input, TextWriter output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));

        _timeManager = new TimeManager(_timeParams);
        _search = new ZapSearch(_params, _timeManager);
        _board = Fen.Parse(Fen.StartPosition);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendLine(string line)
    {
        lock (_outputLock)
        {
            _output.WriteLine(line);
            _output.Flush();
        }
    }

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
        SendLine("id name Zanak");
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

            if (optName.Equals("SearchTraceFile", StringComparison.OrdinalIgnoreCase))
            {
                _search.SearchTraceFile = optVal == "<empty>" ? string.Empty : optVal;
            }
            else if (optName.Equals("PickupTraceFile", StringComparison.OrdinalIgnoreCase))
            {
                _search.PickupTraceFile = optVal == "<empty>" ? string.Empty : optVal;
            }
            else if (optName.Equals("InterleaveRootSeeding", StringComparison.OrdinalIgnoreCase) && bool.TryParse(optVal, out bool interleave))
            {
                _params.InterleaveRootSeeding = interleave;
            }
            else if (optName.Equals("PickupCostWeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int costWeight))
            {
                _params.CrosspointCostWeight = Math.Clamp(costWeight, 0, 100);
            }
            else if (optName.Equals("NnuePath", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _search.SetNnuePath(optVal);
                    _nnueError = null;
                    SendLine($"info string NNUE active directory {_search.NnueEvaluator!.NnueDirectory}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    _nnueError = ex.Message;
                    SendLine($"info string NNUE configuration error: {_nnueError}");
                }
            }
            else if (optName.Equals("Threads", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int threads))
            {
                _search.SetWorkerThreads(threads);
            }
            else if (optName.Equals("Hash", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int hashMb))
            {
                _search.TranspositionTable.Resize(hashMb);
            }
            else if (optName.Equals("ShockThreshold", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int shock))
            {
                _params.ShockThresholdCp = shock;
            }
            else if (optName.Equals("DeadStemMargin", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int margin))
            {
                _params.DeadStemPruneMarginCp = margin;
            }
            else if (optName.Equals("HardAspirationMargin", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int hardAsp))
            {
                _params.HardAspirationMarginCp = hardAsp;
            }
            else if (optName.Equals("ContenderPromotionMargin", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int contPromo))
            {
                _params.ContenderPromotionMarginCp = contPromo;
            }
            else if (optName.Equals("WorkerPruneMargin", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int workPrune))
            {
                _params.WorkerPruneMarginCp = workPrune;
            }
            else if (optName.Equals("AdaptiveShockBase", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int adaptBase))
            {
                _params.AdaptiveShockBaseCp = adaptBase;
            }
            else if (optName.Equals("AdaptiveShockSlope", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int adaptSlope))
            {
                _params.AdaptiveShockSlopeCp = adaptSlope;
            }
            else if (optName.Equals("RecaptureBonus", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int recapBonus))
            {
                _params.RecaptureBonus = recapBonus;
            }
            else if (optName.Equals("EnPriseThreatPenalty", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int enPrisePen))
            {
                _params.EnPriseThreatPenalty = enPrisePen;
            }
            else if (optName.Equals("SpineAbandonThreshold", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int spineAb))
            {
                _params.SpineAbandonThresholdCp = spineAb;
            }
            else if (optName.Equals("CrosspointRootBiasWeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int cpBias))
            {
                _params.CrosspointRootBiasWeight = cpBias;
            }
            else if (optName.Equals("MaxSiblingExploration", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int maxSib))
            {
                _params.MaxSiblingExploration = Math.Clamp(maxSib, 1, 32);
            }
            else if (optName.Equals("HyperBulletBaseDepth", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int hbBd))
            {
                _timeParams.HyperBulletBaseDepth = hbBd;
            }
            else if (optName.Equals("HyperBulletMaxHorizon", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int hbMh))
            {
                _timeParams.HyperBulletMaxHorizon = hbMh;
            }
            else if (optName.Equals("FastBulletBaseDepth", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int fbBd))
            {
                _timeParams.FastBulletBaseDepth = fbBd;
            }
            else if (optName.Equals("FastBulletMaxHorizon", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int fbMh))
            {
                _timeParams.FastBulletMaxHorizon = fbMh;
            }
            else if (optName.Equals("BulletBaseDepth", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int bBd))
            {
                _timeParams.BulletBaseDepth = bBd;
            }
            else if (optName.Equals("BulletMaxHorizon", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int bMh))
            {
                _timeParams.BulletMaxHorizon = bMh;
            }
            else if (optName.Equals("BlitzBaseDepth", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int bzBd))
            {
                _timeParams.BlitzBaseDepth = bzBd;
            }
            else if (optName.Equals("BlitzMaxHorizon", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int bzMh))
            {
                _timeParams.BlitzMaxHorizon = bzMh;
            }
            else if (optName.Equals("RapidBaseDepth", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int rBd))
            {
                _timeParams.RapidBaseDepth = rBd;
            }
            else if (optName.Equals("RapidMaxHorizon", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int rMh))
            {
                _timeParams.RapidMaxHorizon = rMh;
            }
            else if (optName.Equals("AbsoluteMinBaseDepth", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int amBd))
            {
                _timeParams.AbsoluteMinBaseDepth = amBd;
            }
            else if (optName.Equals("AbsoluteMinMaxHorizon", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int amMh))
            {
                _timeParams.AbsoluteMinMaxHorizon = amMh;
            }
            else if (optName.Equals("SuddenDeathDivisor", StringComparison.OrdinalIgnoreCase) && double.TryParse(optVal, out double sdDiv))
            {
                _timeParams.SuddenDeathDivisor = sdDiv;
            }
            else if (optName.Equals("SuddenDeathIncFactor", StringComparison.OrdinalIgnoreCase) && double.TryParse(optVal, out double sdInc))
            {
                _timeParams.SuddenDeathIncFactor = sdInc > 1.0 ? sdInc / 100.0 : sdInc;
            }
            else if (optName.Equals("HardLimitMultiplier", StringComparison.OrdinalIgnoreCase) && double.TryParse(optVal, out double hlMult))
            {
                _timeParams.HardLimitMultiplier = hlMult > 10.0 ? hlMult / 10.0 : hlMult;
            }
            else if (optName.Equals("StableDominanceMinDepth", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int sdMinDepth))
            {
                _timeParams.StableDominanceMinDepth = Math.Clamp(sdMinDepth, 3, 14);
            }
            else if (optName.Equals("StableDominanceLeadCp", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int sdLead))
            {
                _timeParams.StableDominanceLeadCp = Math.Clamp(sdLead, 40, 400);
            }
            else if (optName.Equals("StableDominanceIters", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int sdIters))
            {
                _timeParams.StableDominanceIters = Math.Clamp(sdIters, 1, 8);
            }
            else if (optName.Equals("StableDominanceMinSoftPct", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int sdMinSoftPct))
            {
                _timeParams.StableDominanceMinSoftFraction = Math.Clamp(sdMinSoftPct, 5, 70) / 100.0;
            }
            else if (optName.Equals("PanicExtensionGapCp", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int panicGap))
            {
                _timeParams.PanicExtensionGapCp = Math.Clamp(panicGap, 0, 60);
            }
            else if (optName.Equals("PanicExtensionFactorPct", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int panicFactorPct))
            {
                _timeParams.PanicExtensionFactor = Math.Clamp(panicFactorPct, 100, 250) / 100.0;
            }
            else if (optName.Equals("SurvivorSoftScale2Pct", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int surv2Pct))
            {
                _timeParams.SurvivorSoftScale2 = Math.Clamp(surv2Pct, 10, 95) / 100.0;
            }
            else if (optName.Equals("SurvivorSoftScale3Pct", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int surv3Pct))
            {
                _timeParams.SurvivorSoftScale3 = Math.Clamp(surv3Pct, 15, 95) / 100.0;
            }
            else if (optName.Equals("SurvivorSoftScale4Pct", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int surv4Pct))
            {
                _timeParams.SurvivorSoftScale4 = Math.Clamp(surv4Pct, 25, 100) / 100.0;
            }
            else if (optName.Equals("SmallSurvivorMinSoftPct", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int smallMinSoftPct))
            {
                _timeParams.SmallSurvivorMinSoftFraction = Math.Clamp(smallMinSoftPct, 2, 50) / 100.0;
            }
            else if (optName.Equals("DominantLeaderLeadCp", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int domLead))
            {
                _timeParams.DominantLeaderLeadCp = Math.Clamp(domLead, 40, 400);
            }
            else if (optName.Equals("SmallSurvivorVerifyCrosspoints", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int smallVerify))
            {
                _timeParams.SmallSurvivorVerifyCrosspoints = Math.Clamp(smallVerify, 2, 20);
            }
            else if (optName.Equals("LeaderVerifyCrosspoints", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int leaderVerify))
            {
                _timeParams.LeaderVerifyCrosspoints = Math.Clamp(leaderVerify, 4, 35);
            }
            else if (optName.Equals("ContenderQuickCheckPerRoot", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int quickCheck))
            {
                _timeParams.ContenderQuickCheckPerRoot = Math.Clamp(quickCheck, 1, 10);
            }
            else if (optName.Equals("SyzygyPath", StringComparison.OrdinalIgnoreCase))
            {
                if (_search.Tablebase is Anak.Data.Syzygy.SyzygyTablebase stb)
                {
                    stb.SyzygyPath = optVal;
                }
            }
            else if (optName.Equals("SyzygyProbeDepth", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int probeDepth))
            {
                _params.SyzygyProbeDepth = probeDepth;
                if (_search.Tablebase != null)
                {
                    _search.Tablebase.SyzygyProbeDepth = probeDepth;
                }
            }
        }
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
                infinite: infinite);

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

    public void Dispose()
    {
        StopSearch();
        _search.Dispose();
    }
}
