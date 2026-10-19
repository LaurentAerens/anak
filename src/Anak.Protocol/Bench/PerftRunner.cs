using System.Diagnostics;
using Anak.Core;

namespace Anak.Protocol.Bench;

/// <summary>
/// High-performance zero-allocation Perft (Performance Test) runner and move-divide diagnostic harness.
/// </summary>
public static class PerftRunner
{
    /// <summary>
    /// Executes Perft divide to the specified depth on the given board position,
    /// printing the move breakdown and total node count / NPS.
    /// </summary>
    /// <param name="board">Starting chess position.</param>
    /// <param name="depth">Target Perft search depth.</param>
    /// <param name="output">Optional output action for reporting results line by line.</param>
    /// <returns>Total leaf node count searched.</returns>
    public static ulong Divide(ref Board board, int depth, Action<string>? output = null)
    {
        if (depth <= 0)
        {
            output?.Invoke("Nodes searched: 1 in 0 ms (0 nps)");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        ulong totalNodes = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            Move move = moves[i];
            board.MakeMove(move, out BoardState state);

            ulong nodes = depth == 1 ? 1 : Perft(ref board, depth - 1);
            board.UnmakeMove(move, in state);

            totalNodes += nodes;
            output?.Invoke($"{move.ToUci()}: {nodes}");
        }

        sw.Stop();
        long elapsedMs = Math.Max(1, sw.ElapsedMilliseconds);
        ulong nps = (totalNodes * 1000UL) / (ulong)elapsedMs;

        output?.Invoke(string.Empty);
        output?.Invoke($"Nodes searched: {totalNodes} in {elapsedMs} ms ({nps:N0} nps)");

        return totalNodes;
    }

    /// <summary>
    /// Recursive zero-allocation Perft node counter.
    /// </summary>
    /// <param name="board">Active chess position.</param>
    /// <param name="depth">Remaining depth to traverse.</param>
    /// <returns>Total number of leaf positions at target depth.</returns>
    public static ulong Perft(ref Board board, int depth)
    {
        if (depth <= 0) return 1;

        MoveList moves = default;
        board.GenerateLegalMoves(ref moves);

        if (depth == 1) return (ulong)moves.Count;

        ulong nodes = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            Move move = moves[i];
            board.MakeMove(move, out BoardState state);
            nodes += Perft(ref board, depth - 1);
            board.UnmakeMove(move, in state);
        }

        return nodes;
    }
}
