using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Flags representing the type of score or state stored in a Transposition Table entry.
/// </summary>
[Flags]
public enum TTFlags : byte
{
    None = 0,
    Exact = 1,
    LowerBound = 2,
    UpperBound = 3,
    Terminal = 4
}

/// <summary>
/// Packed 32-byte entry for the Transposition Table.
/// Dual-compatible with both MCTS (Visits, Value, Prior) and Minimax / Alpha-Beta (Depth, Score, Bound).
/// Two entries fit exactly into a 64-byte CPU cache line.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct TTEntry
{
    public const short MateBound = 29000;
    public const short MateScore = 30000;

    /// <summary>
    /// 64-bit Zobrist hash key of the chess position.
    /// </summary>
    public ulong Key;

    /// <summary>
    /// Total visits accumulated across convergent DAG paths for this position (MCTS).
    /// In Minimax, holds at least 1 or search depth for compatibility.
    /// </summary>
    public uint Visits;

    /// <summary>
    /// Mean value Q in [-1.0, 1.0] from the perspective of the side to move (MCTS).
    /// </summary>
    public float Value;

    /// <summary>
    /// Prior probability or policy prior for this state (MCTS).
    /// </summary>
    public float Prior;

    /// <summary>
    /// Centipawn score relative to side to move (Minimax).
    /// </summary>
    public short Score;

    /// <summary>
    /// Best move found from this position.
    /// </summary>
    public Move BestMove;

    /// <summary>
    /// Search depth (Minimax).
    /// </summary>
    public byte Depth;

    /// <summary>
    /// Search generation / age when this entry was created or updated.
    /// </summary>
    public byte Age;

    /// <summary>
    /// Flags indicating bound or terminal state.
    /// </summary>
    public TTFlags Flags;

    /// <summary>
    /// Padding fields to ensure struct size is exactly 32 bytes.
    /// 8 + 4 + 4 + 4 + 2 + 2 + 1 + 1 + 1 + 1 + 4 = 32 bytes.
    /// </summary>
    private readonly byte _pad0;
    private readonly uint _pad1;

    /// <summary>
    /// Minimax / Alpha-Beta constructor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TTEntry(ulong key, Move bestMove, byte depth, short score, byte age, TTFlags flags)
    {
        Key = key;
        Visits = Math.Max(1u, (uint)depth);
        Value = 0.0f;
        Prior = 0.0f;
        Score = score;
        BestMove = bestMove;
        Depth = depth;
        Age = age;
        Flags = flags;
        _pad0 = 0;
        _pad1 = 0;
    }

    /// <summary>
    /// MCTS constructor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TTEntry(ulong key, Move bestMove, uint visits, float value, float prior, byte age, TTFlags flags = TTFlags.Exact)
    {
        Key = key;
        Visits = visits;
        Value = value;
        Prior = prior;
        Score = 0;
        BestMove = bestMove;
        Depth = 0;
        Age = age;
        Flags = flags;
        _pad0 = 0;
        _pad1 = 0;
    }

    /// <summary>
    /// Converts a search score to a ply-independent TT score for mate distance adjustment.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short ScoreToTt(int score, int ply)
    {
        if (score >= MateBound)
            return (short)(score + ply);
        if (score <= -MateBound)
            return (short)(score - ply);
        return (short)score;
    }

    /// <summary>
    /// Converts a ply-independent TT score back to a current-ply search score.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ScoreFromTt(int score, int ply)
    {
        if (score >= MateBound)
            return score - ply;
        if (score <= -MateBound)
            return score + ply;
        return score;
    }

    /// <summary>
    /// Returns true if this entry contains valid search data.
    /// </summary>
    public readonly bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Key != 0 && (Visits > 0 || Depth > 0 || Flags != TTFlags.None);
    }

    /// <summary>
    /// Resets the entry.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        Key = 0;
        Visits = 0;
        Value = 0.0f;
        Prior = 0.0f;
        Score = 0;
        BestMove = Move.Null;
        Depth = 0;
        Age = 0;
        Flags = TTFlags.None;
    }
}
