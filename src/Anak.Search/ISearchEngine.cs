using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Unified contract for chess search engines (Alpha-Beta and MCTS).
/// </summary>
public interface ISearchEngine
{
    /// <summary>
    /// Gets the shared Transposition Table instance.
    /// </summary>
    TranspositionTable TranspositionTable { get; }

    /// <summary>
    /// Gets or sets the number of concurrent search worker threads.
    /// </summary>
    int Threads { get; set; }

    /// <summary>
    /// Executes search for the given board position within the specified limits.
    /// </summary>
    Move Search(
        in Board board,
        in SearchLimits limits,
        Action<SearchStatistics>? infoCallback = null,
        CancellationToken cancellationToken = default);
}
