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
/// Two entries fit exactly into a 64-byte CPU cache line.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct TTEntry
{
    /// <summary>
    /// 64-bit Zobrist hash key of the chess position.
    /// </summary>
    public ulong Key;

    /// <summary>
    /// Total visits accumulated across convergent DAG paths for this position.
    /// </summary>
    public uint Visits;

    /// <summary>
    /// Mean value Q in [-1.0, 1.0] from the perspective of the side to move.
    /// </summary>
    public float Value;

    /// <summary>
    /// Prior probability or policy prior for this state.
    /// </summary>
    public float Prior;

    /// <summary>
    /// Best move found from this position.
    /// </summary>
    public Move BestMove;

    /// <summary>
    /// Search generation / age when this entry was created or updated.
    /// </summary>
    public byte Age;

    /// <summary>
    /// Flags indicating bound or terminal state.
    /// </summary>
    public TTFlags Flags;

    /// <summary>
    /// 8-byte padding to align struct size to exactly 32 bytes (24 + 8 = 32).
    /// </summary>
    private readonly ulong _pad;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TTEntry(ulong key, Move bestMove, uint visits, float value, float prior, byte age, TTFlags flags = TTFlags.Exact)
    {
        Key = key;
        Visits = visits;
        Value = value;
        Prior = prior;
        BestMove = bestMove;
        Age = age;
        Flags = flags;
        _pad = 0;
    }

    /// <summary>
    /// Returns true if this entry contains valid search data.
    /// </summary>
    public readonly bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Visits > 0 && Key != 0;
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
        BestMove = Move.Null;
        Age = 0;
        Flags = TTFlags.None;
    }
}
