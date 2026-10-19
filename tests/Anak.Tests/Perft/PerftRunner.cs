using System;
using System.Diagnostics;
using Anak.Core;

namespace Anak.Tests.Perft;

/// <summary>
/// High-performance recursive Perft engine for move generation validation and benchmarking.
/// </summary>
public static class PerftRunner
{
    /// <summary>
    /// Recursive Perft calculating total leaf nodes at the specified depth.
    /// </summary>
    public static ulong Perft(ref Board board, int depth)
    {
        if (depth <= 0)
            return 1UL;

        MoveList moveList = new();
        board.GenerateLegalMoves(ref moveList);

        if (depth == 1)
            return (ulong)moveList.Count;

        ulong nodes = 0;
        int count = moveList.Count;
        for (int i = 0; i < count; i++)
        {
            Move move = moveList[i];
            board.MakeMove(move, out BoardState state);
            nodes += Perft(ref board, depth - 1);
            board.UnmakeMove(move, in state);
        }

        return nodes;
    }

    /// <summary>
    /// Overload for calling Perft without explicitly passing ref.
    /// </summary>
    public static ulong Perft(Board board, int depth) => Perft(ref board, depth);

    /// <summary>
    /// Perft Divide: calculates sub-tree leaf counts for each root move, reporting to an optional callback.
    /// </summary>
    public static ulong PerftDivide(ref Board board, int depth, Action<Move, ulong>? onMove = null)
    {
        if (depth <= 0)
            return 1UL;

        MoveList moveList = new();
        board.GenerateLegalMoves(ref moveList);

        ulong totalNodes = 0;
        int count = moveList.Count;
        for (int i = 0; i < count; i++)
        {
            Move move = moveList[i];
            board.MakeMove(move, out BoardState state);
            ulong subNodes = (depth == 1) ? 1 : Perft(ref board, depth - 1);
            board.UnmakeMove(move, in state);

            totalNodes += subNodes;
            onMove?.Invoke(move, subNodes);
        }

        return totalNodes;
    }

    /// <summary>
    /// Overload for calling PerftDivide without explicitly passing ref.
    /// </summary>
    public static ulong PerftDivide(Board board, int depth, Action<Move, ulong>? onMove = null) =>
        PerftDivide(ref board, depth, onMove);

    /// <summary>
    /// Runs a timed perft execution and computes Million Nodes Per Second (MNPS).
    /// </summary>
    public static PerftResult Benchmark(ref Board board, int depth)
    {
        var sw = Stopwatch.StartNew();
        ulong nodes = Perft(ref board, depth);
        sw.Stop();

        double elapsedSeconds = sw.Elapsed.TotalSeconds;
        double mnps = elapsedSeconds > 0 ? (nodes / 1_000_000.0) / elapsedSeconds : 0.0;
        return new PerftResult(nodes, sw.Elapsed, mnps);
    }

    /// <summary>
    /// Overload for calling Benchmark without explicitly passing ref.
    /// </summary>
    public static PerftResult Benchmark(Board board, int depth) => Benchmark(ref board, depth);
}

/// <summary>
/// Result of a timed Perft run.
/// </summary>
public readonly record struct PerftResult(ulong Nodes, TimeSpan Elapsed, double Mnps);
