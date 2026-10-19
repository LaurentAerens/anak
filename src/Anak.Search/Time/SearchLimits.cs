using System.Runtime.InteropServices;

namespace Anak.Search;

/// <summary>
/// Encapsulates search limits passed from the UCI protocol or external caller.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SearchLimits
{
    /// <summary>
    /// White's remaining time on clock in milliseconds (-1 if not specified).
    /// </summary>
    public int WhiteTime;

    /// <summary>
    /// Black's remaining time on clock in milliseconds (-1 if not specified).
    /// </summary>
    public int BlackTime;

    /// <summary>
    /// White's time increment per move in milliseconds.
    /// </summary>
    public int WhiteIncrement;

    /// <summary>
    /// Black's time increment per move in milliseconds.
    /// </summary>
    public int BlackIncrement;

    /// <summary>
    /// Exact target time allocated for this single move in milliseconds (-1 if not specified).
    /// </summary>
    public int MoveTime;

    /// <summary>
    /// Number of moves remaining until the next time control (0 if sudden death).
    /// </summary>
    public int MovesToGo;

    /// <summary>
    /// Maximum search depth (-1 or 0 if unlimited).
    /// </summary>
    public int Depth;

    /// <summary>
    /// Maximum node count to search (0 if unlimited).
    /// </summary>
    public ulong Nodes;

    /// <summary>
    /// True if search should run infinitely until explicitly stopped.
    /// </summary>
    public bool Infinite;

    public SearchLimits()
    {
        WhiteTime = -1;
        BlackTime = -1;
        WhiteIncrement = 0;
        BlackIncrement = 0;
        MoveTime = -1;
        MovesToGo = 0;
        Depth = -1;
        Nodes = 0;
        Infinite = false;
    }

    /// <summary>
    /// Returns true if a time limit (clock or movetime) was specified.
    /// </summary>
    public readonly bool HasTimeLimit => MoveTime > 0 || WhiteTime > 0 || BlackTime > 0;

    /// <summary>
    /// Returns true if a maximum node limit was specified.
    /// </summary>
    public readonly bool HasNodeLimit => Nodes > 0;

    /// <summary>
    /// Returns true if a maximum depth limit was specified.
    /// </summary>
    public readonly bool HasDepthLimit => Depth > 0;
}
