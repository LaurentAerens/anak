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
/// Packed 20-byte entry for the Alpha-Beta Transposition Table.
/// Three entries fit into a single 64-byte CPU cache line (60 bytes + 4 bytes cluster padding).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 20)]
public struct TTEntry
{
    public const short MateBound = 29000;
    public const short MateScore = 30000;

    /// <summary>
    /// 64-bit Zobrist hash key of the chess position.
    /// </summary>
    public ulong Key;

    /// <summary>
    /// Centipawn score relative to side to move.
    /// </summary>
    public short Score;

    /// <summary>
    /// Cached raw static evaluation (centipawns) relative to side to move, or short.MinValue if uncomputed.
    /// </summary>
    public short StaticEval;

    /// <summary>
    /// Best move found from this position.
    /// </summary>
    public Move BestMove;

    /// <summary>
    /// Search depth.
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
    /// ID of the specialized NNUE expert model that evaluated or searched this entry (0 in unified ArchMoE mode).
    /// </summary>
    public byte ExpertId;

    /// <summary>
    /// Padding field to ensure struct size is exactly 20 bytes (8 + 2 + 2 + 2 + 1 + 1 + 1 + 1 + 2 = 20).
    /// </summary>
    private readonly short _pad1;

    /// <summary>
    /// Alpha-Beta Transposition Table entry constructor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TTEntry(ulong key, Move bestMove, byte depth, short score, byte age, TTFlags flags, byte expertId = 0, short staticEval = short.MinValue)
    {
        Key = key;
        Score = score;
        StaticEval = staticEval;
        BestMove = bestMove;
        Depth = depth;
        Age = age;
        Flags = flags;
        ExpertId = expertId;
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
        get => Key != 0 && (Depth > 0 || Flags != TTFlags.None);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short ClampEval(int rawEval)
        => rawEval != -Value.InfiniteValue ? (short)Math.Clamp(rawEval, -30000, 30000) : short.MinValue;

    /// <summary>
    /// Resets the entry.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        Key = 0;
        Score = 0;
        StaticEval = short.MinValue;
        BestMove = Move.Null;
        Depth = 0;
        Age = 0;
        Flags = TTFlags.None;
        ExpertId = 0;
    }
}
