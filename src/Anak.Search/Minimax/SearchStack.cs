using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Per-ply search information stored on the search stack.
/// Contains move history, killer moves, singular extension exclusions, and static evaluations.
/// </summary>
public struct SearchStack
{
    /// <summary>
    /// Current move being searched at this ply.
    /// </summary>
    public Move CurrentMove;

    /// <summary>
    /// Piece being moved by CurrentMove.
    /// </summary>
    public Piece MovedPiece;

    /// <summary>
    /// Move excluded from search at this node (used for Singular Extensions).
    /// </summary>
    public Move ExcludedMove;

    /// <summary>
    /// Primary killer move at this ply (quiet move that caused a beta cutoff).
    /// </summary>
    public Move Killer0;

    /// <summary>
    /// Secondary killer move at this ply.
    /// </summary>
    public Move Killer1;

    /// <summary>
    /// Static evaluation of the position before move making.
    /// </summary>
    public int StaticEval;

    /// <summary>
    /// Number of legal moves played/searched at this ply.
    /// </summary>
    public int MoveCount;

    /// <summary>
    /// Indicates whether the side to move is in check at this ply.
    /// </summary>
    public bool InCheck;

    /// <summary>
    /// Resets all fields in the stack frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        CurrentMove = Move.Null;
        MovedPiece = Piece.None;
        ExcludedMove = Move.Null;
        Killer0 = Move.Null;
        Killer1 = Move.Null;
        StaticEval = 0;
        MoveCount = 0;
        InCheck = false;
    }

    /// <summary>
    /// Adds a quiet move as a killer move, shifting the primary killer to secondary.
    /// </summary>
    /// <param name="move">The quiet move that caused a beta cutoff.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddKiller(Move move)
    {
        if (Killer0 != move)
        {
            Killer1 = Killer0;
            Killer0 = move;
        }
    }
}
