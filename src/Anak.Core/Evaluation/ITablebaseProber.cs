namespace Anak.Core;

/// <summary>
/// Prober interface for Syzygy and endgame tablebases (WDL and DTZ).
/// Enables search engines to probe tablebase results without direct dependency on storage backends.
/// </summary>
public interface ITablebaseProber
{
    /// <summary>
    /// Maximum piece count of loaded tablebases (e.g. 4, 5, or 6).
    /// </summary>
    int MaxPieces { get; }

    /// <summary>
    /// Minimum search depth at which WDL tablebases are probed during search tree recursion.
    /// </summary>
    int SyzygyProbeDepth { get; set; }

    /// <summary>
    /// Probes the Win-Draw-Loss value for the given position.
    /// Returns true on a successful tablebase hit.
    /// </summary>
    /// <param name="board">Board position to probe.</param>
    /// <param name="wdl">WDL value: 2 (win), 1 (cursed win), 0 (draw), -1 (blessed loss), -2 (loss).</param>
    bool TryProbeWdl(in Board board, out int wdl);

    /// <summary>
    /// Probes the root position: returns the tablebase-best move and the DTZ of the position.
    /// </summary>
    /// <param name="board">Board position to probe.</param>
    /// <param name="bestMove">Optimal move selected by WDL / DTZ criteria.</param>
    /// <param name="dtz">Distance in plies to zeroing move (signed).</param>
    bool TryProbeDtz(in Board board, out Move bestMove, out int dtz);
}
