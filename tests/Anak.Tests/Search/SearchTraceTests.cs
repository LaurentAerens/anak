using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Anak.Core;
using Anak.Data.Evaluation;
using Anak.Protocol.Uci;
using Anak.Search;
using Xunit;
using Zanak.Search;
using Zanak.Time;
using File = System.IO.File;

namespace Anak.Tests.Search;

public sealed class SearchTraceTests
{
    [Fact]
    public void AlphaBeta_TraceIncludesEveryRootMove_NotOnlyPv_AndRealKeys()
    {
        string path = TempPath();
        try
        {
            Board board = Fen.Parse(Fen.StartPosition);
            var engine = new AlphaBetaEngine(new DefaultEvaluator(), defaultHashMb: 1) { SearchTraceFile = path };
            Move best = engine.Search(in board, new SearchLimits { Depth = 2 });
            var untraced = new AlphaBetaEngine(new DefaultEvaluator(), defaultHashMb: 1);
            Assert.Equal(best, untraced.Search(in board, new SearchLimits { Depth = 2 }));
            string[] lines = File.ReadAllLines(path);
            Assert.Equal($"{{\"root\":\"{board.ZobristKey:X16}\"}}", lines[0]);
            MoveList legal = default;
            board.GenerateLegalMoves(ref legal);
            foreach (Move move in legal.AsSpan())
            {
                Board child = board;
                Assert.True(child.MakeMove(move, out _));
                AssertEdge(lines, board.ZobristKey, child.ZobristKey, move);
            }
            Assert.Contains(lines.Skip(1).SkipLast(1), line => !line.Contains($"\"move\":\"{best.ToUci()}\""));
            Assert.True(lines.Length > legal.Count + 1);
            AssertRealGraph(board, lines);
            AssertExclusiveAccess(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AlphaBeta_TraceClosesOnSearchError_AndCanBeDisabled()
    {
        string path = TempPath();
        try
        {
            Board board = Fen.Parse(Fen.StartPosition);
            var engine = new AlphaBetaEngine(new DefaultEvaluator(), defaultHashMb: 1)
            {
                SearchTraceFile = path,
                Threads = 2
            };
            Assert.Throws<InvalidOperationException>(() => engine.Search(in board, new SearchLimits { Depth = 1 },
                _ => throw new InvalidOperationException("test callback")));
            AssertExclusiveAccess(path);
            string previous = File.ReadAllText(path);
            engine.SearchTraceFile = string.Empty;
            engine.Search(in board, new SearchLimits { Depth = 1 });
            Assert.Equal(previous, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Zanak_TraceIncludesRootSeeds_AndEveryProbeContinuation()
    {
        string path = TempPath();
        try
        {
            Board board = Fen.Parse(Fen.StartPosition);
            using var search = new ZapSearch(new ZapParams { DefaultThreads = 1 }, evaluator: new DefaultEvaluator())
            {
                SearchTraceFile = path
            };
            var budget = new SearchBudget(1000, 2000, 3, 3);
            // Stop after seeding, so the complete published trajectories are the actual probes.
            using var cts = new CancellationTokenSource();
            search.Search(in board, in budget, _ => cts.Cancel(), cts.Token);
            string[] lines = File.ReadAllLines(path);
            Assert.Equal($"{{\"root\":\"{board.ZobristKey:X16}\"}}", lines[0]);
            int seeded = 0;
            for (int i = 0; i < search.SharedState.CandidateCount; i++)
            {
                ZapRootCandidate candidate = search.SharedState.GetCandidate(i);
                if (candidate.Trajectory.Length == 0) continue;
                seeded++;
                Board child = board;
                Assert.True(child.MakeMove(candidate.Move, out _));
                AssertEdge(lines, board.ZobristKey, child.ZobristKey, candidate.Move);
                foreach (ZapTrajectoryStep step in candidate.Trajectory)
                {
                    ulong before = child.ZobristKey;
                    Assert.Equal(step.ZobristKey, before);
                    Assert.True(child.MakeMove(step.Move, out _));
                    AssertEdge(lines, before, child.ZobristKey, step.Move);
                }
            }
            Assert.True(seeded > 1);
            AssertExclusiveAccess(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Zanak_SingleThreadTrace_RunsSeedsAndAgentsOnTheCaller()
    {
        string path = TempPath();
        try
        {
            Board board = Fen.Parse("7k/8/3p4/2pP4/1pP5/1P6/8/K7 w - - 0 45");
            var evaluator = new ThreadRecordingEvaluator();
            using var search = new ZapSearch(new ZapParams { DefaultThreads = 1 }, evaluator: evaluator)
            {
                SearchTraceFile = path
            };
            int agents = 0;
            search.SharedState.AgentTraced += _ => Interlocked.Increment(ref agents);
            int caller = Environment.CurrentManagedThreadId;
            var budget = new SearchBudget(1000, 2000, 3, 3);
            search.Search(in board, in budget);
            Assert.True(agents > 0);
            Assert.Equal(new[] { caller }, evaluator.Threads.Keys.Order().ToArray());
            AssertRealGraph(board, File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Zanak_AgentTraceIncludesEverySiblingJunction_AndItsContinuation(int horizon)
    {
        string path = TempPath();
        try
        {
            Board root = Fen.Parse(Fen.StartPosition);
            Move rootMove = LegalMove(root, "e2e4");
            Board junction = root;
            Assert.True(junction.MakeMove(rootMove, out _));
            Move original = LegalMove(junction, "e7e5");
            var parameters = new ZapParams { DefaultThreads = 1, RootFilterMarginCp = 0, QuiescenceMaxPlies = 0, MaxSiblingExploration = 32 };
            var state = new ZapSharedState();
            state.Reset([rootMove]);
            uint generation = state.NextGeneration();
            var evaluator = new DefaultEvaluator();
            using (var trace = SearchTraceWriter.Open(path, junction.ZobristKey))
            using (var pool = new ZapWorkerPool(1, new ZapWorkQueue(parameters), state, parameters, evaluator))
            {
                pool.SearchTrace = trace;
                pool.SetRootBoard(in root);
                var budget = new SearchBudget(1000, 2000, horizon, horizon);
                pool.StartExploration(Color.White, in budget, runOnCaller: true);
                try
                {
                    var cp = new ZapCrosspoint(junction, 0, 1, 0, 0, CrosspointKind.AdversarialRefutation,
                        horizon, original, generation: generation);
                    pool.ProcessCrosspoint(in cp, new ZapPolicy(parameters), evaluator);
                }
                finally { pool.StopExploration(); }
            }
            string[] lines = File.ReadAllLines(path);
            MoveList siblings = default;
            junction.GenerateLegalMoves(ref siblings);
            foreach (Move sibling in siblings.AsSpan())
            {
                Board child = junction;
                Assert.True(child.MakeMove(sibling, out _));
                AssertEdge(lines, junction.ZobristKey, child.ZobristKey, sibling);
                if (horizon > 1)
                    Assert.Contains(lines.Skip(1), line => line.StartsWith($"{{\"from\":\"{child.ZobristKey:X16}\""));
            }
            AssertRealGraph(junction, lines);
            AssertExclusiveAccess(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Quiescence_TraceRecordsActualCapture()
    {
        string path = TempPath();
        try
        {
            Board board = Fen.Parse("4k3/8/8/8/8/8/4q3/4R1K1 w - - 0 1");
            Board child = board;
            Move capture = LegalMove(board, "e1e2");
            Assert.True(child.MakeMove(capture, out _));
            using (var trace = SearchTraceWriter.Open(path, board.ZobristKey))
            {
                var quiescence = new QuiescenceSearch(new DefaultEvaluator()) { SearchTrace = trace };
                quiescence.Search(ref board, -32000, 32000);
            }
            AssertEdge(File.ReadAllLines(path), board.ZobristKey, child.ZobristKey, capture);
            AssertExclusiveAccess(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Zanak_ImmediateMateSeed_RecordsRootEdgeWithoutContinuation()
    {
        string path = TempPath();
        try
        {
            // Nine pieces keep this test outside root tablebase fast paths.
            Board board = Fen.Parse("7k/ppp5/5KQ1/8/8/8/PPP5/8 w - - 0 1");
            using var search = new ZapSearch(new ZapParams { DefaultThreads = 1 }, evaluator: new DefaultEvaluator())
            {
                SearchTraceFile = path
            };
            var budget = new SearchBudget(1000, 2000, 2, 2);
            Move best = search.Search(in board, in budget);
            Board child = board;
            Assert.True(child.MakeMove(best, out _));
            MoveList replies = default;
            child.GenerateLegalMoves(ref replies);
            Assert.Equal(0, replies.Count);
            Assert.True(child.IsInCheck());
            AssertEdge(File.ReadAllLines(path), board.ZobristKey, child.ZobristKey, best);
            AssertExclusiveAccess(path);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TerminalSearch_TruncatesPreviousTrace_AndCloses(bool anak)
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "old trace");
            Board board = Fen.Parse("7k/6Q1/6K1/8/8/8/8/8 b - - 0 1");
            if (anak)
            {
                var engine = new AlphaBetaEngine(new DefaultEvaluator(), defaultHashMb: 1) { SearchTraceFile = path };
                Assert.True(engine.Search(in board, new SearchLimits { Depth = 1 }).IsNull);
            }
            else
            {
                using var search = new ZapSearch(evaluator: new DefaultEvaluator()) { SearchTraceFile = path };
                var budget = new SearchBudget(100, 200, 2, 2);
                Assert.True(search.Search(in board, in budget).IsNull);
            }
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Equal($"{{\"root\":\"{board.ZobristKey:X16}\"}}", lines[0]);
            using var end = JsonDocument.Parse(lines[1]);
            Assert.True(end.RootElement.GetProperty("elapsed_seconds").GetDouble() >= 0);
            AssertExclusiveAccess(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TraceWriter_SerializesConcurrentVisits_IncludingHeuristicNullMoves()
    {
        string path = TempPath();
        try
        {
            Board board = Fen.Parse(Fen.StartPosition);
            Board child = board;
            Assert.True(child.MakeNullMove(out _));
            using (var trace = SearchTraceWriter.Open(path, board.ZobristKey))
            {
                Parallel.For(0, 64, _ => trace!.WriteEdge(board.ZobristKey, child.ZobristKey, Move.Null));
            }
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(66, lines.Length);
            Assert.All(lines.Skip(1).SkipLast(1), line =>
                Assert.StartsWith(Edge(board.ZobristKey, child.ZobristKey, Move.Null)[..^1] + ",\"seconds\":", line));
            AssertRealGraph(board, lines);
            AssertExclusiveAccess(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TraceWriter_TimesVisitsAndSearchEnd_WithInvariantNumbers()
    {
        string path = TempPath();
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Board board = Fen.Parse(Fen.StartPosition);
            Move move = LegalMove(board, "e2e4");
            Board child = board;
            Assert.True(child.MakeMove(move, out _));
            using (var trace = SearchTraceWriter.Open(path, board.ZobristKey))
            {
                trace!.WriteEdge(board.ZobristKey, child.ZobristKey, move);
                Thread.Sleep(10);
                trace.WriteEdge(board.ZobristKey, child.ZobristKey, move);
                Thread.Sleep(10);
            }
            string[] lines = File.ReadAllLines(path);
            using var first = JsonDocument.Parse(lines[1]);
            using var second = JsonDocument.Parse(lines[2]);
            using var end = JsonDocument.Parse(lines[3]);
            double firstSeconds = first.RootElement.GetProperty("seconds").GetDouble();
            double secondSeconds = second.RootElement.GetProperty("seconds").GetDouble();
            double elapsed = end.RootElement.GetProperty("elapsed_seconds").GetDouble();
            Assert.True(secondSeconds - firstSeconds >= 0.005);
            Assert.True(elapsed - secondSeconds >= 0.005);
            AssertExclusiveAccess(path);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            File.Delete(path);
        }
    }

    [Fact]
    public void TraceOption_DefaultsOff_AndInvalidPathIsNotSilentlyIgnored()
    {
        Assert.Equal("option name SearchTraceFile type string default <empty>",
            new UciOptionRegistry().SearchTraceFile.GetUciDeclaration());
        Assert.Null(SearchTraceWriter.Open(string.Empty, 0));
        Assert.Null(SearchTraceWriter.Open("<empty>", 0));
        Board board = Fen.Parse(Fen.StartPosition);
        var engine = new AlphaBetaEngine(new DefaultEvaluator(), defaultHashMb: 1)
        {
            SearchTraceFile = Path.Combine(TempPath(), "missing", "trace.jsonl")
        };
        Assert.Throws<DirectoryNotFoundException>(() => engine.Search(in board, new SearchLimits { Depth = 1 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnakUci_TraceIsClosedBeforeBestMove_WithHelpers(bool immediateStop)
    {
        string path = TempPath();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new UciEngineAdapter(line =>
        {
            if (!line.StartsWith("bestmove ")) return;
            try
            {
                AssertExclusiveAccess(path);
                string[] lines = File.ReadAllLines(path);
                Assert.StartsWith("{\"root\":\"", lines[0]);
                if (!immediateStop) Assert.True(lines.Length > 2);
                completed.TrySetResult(true);
            }
            catch (Exception ex) { completed.TrySetException(ex); }
        });
        try
        {
            adapter.Options.TrySetValue("OwnBook", "false");
            adapter.Options.TrySetValue("Threads", "2");
            adapter.Options.TrySetValue("UseNnue", "false");
            adapter.Options.TrySetValue("SearchTraceFile", path);
            adapter.StartSearch(new SearchLimits { Depth = 2 });
            if (immediateStop) adapter.StopSearch();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            adapter.StopSearch();
            string trace = File.ReadAllText(path);
            adapter.Warmup();
            Assert.Equal(trace, File.ReadAllText(path));
        }
        finally
        {
            adapter.StopSearch();
            File.Delete(path);
        }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"anak-trace-{Guid.NewGuid():N}.jsonl");
    private static string Edge(ulong from, ulong to, Move move) =>
        $"{{\"from\":\"{from:X16}\",\"to\":\"{to:X16}\",\"move\":\"{(move.IsNull ? "0000" : move.ToUci())}\"}}";

    private static void AssertEdge(string[] lines, ulong from, ulong to, Move move) =>
        Assert.Contains(lines, line => line.StartsWith(Edge(from, to, move)[..^1] + ",\"seconds\":", StringComparison.Ordinal));

    private sealed class ThreadRecordingEvaluator : IEvaluator
    {
        private readonly DefaultEvaluator _inner = new();
        public ConcurrentDictionary<int, byte> Threads { get; } = new();

        public Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves)
        {
            Threads.TryAdd(Environment.CurrentManagedThreadId, 0);
            Thread.Sleep(1);
            return _inner.Evaluate(in board, legalMoves);
        }
    }

    private static void AssertExclusiveAccess(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(stream.Length > 0);
    }

    private static Move LegalMove(Board board, string uci)
    {
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);
        foreach (Move move in moves.AsSpan())
            if (move.ToUci() == uci) return move;
        throw new InvalidOperationException($"Illegal test move {uci}");
    }

    private static void AssertRealGraph(Board root, string[] lines)
    {
        var positions = new Dictionary<string, Board> { [root.ZobristKey.ToString("X16")] = root };
        double lastSeconds = 0;
        foreach (string line in lines.Skip(1))
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("elapsed_seconds", out JsonElement elapsed))
            {
                Assert.True(elapsed.GetDouble() >= lastSeconds);
                Assert.Equal(lines[^1], line);
                continue;
            }
            double seconds = json.RootElement.GetProperty("seconds").GetDouble();
            Assert.True(seconds >= lastSeconds);
            lastSeconds = seconds;
            string from = json.RootElement.GetProperty("from").GetString()!;
            string to = json.RootElement.GetProperty("to").GetString()!;
            string uci = json.RootElement.GetProperty("move").GetString()!;
            Assert.True(positions.TryGetValue(from, out Board board), $"Disconnected edge: {line}");
            if (uci == "0000") Assert.True(board.MakeNullMove(out _));
            else Assert.True(board.MakeMove(LegalMove(board, uci), out _));
            Assert.Equal(to, board.ZobristKey.ToString("X16"));
            positions[to] = board;
        }
    }
}
